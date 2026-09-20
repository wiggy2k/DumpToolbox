using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
private static async Task<IReadOnlyList<HashManifestEntry>> ReadHashManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var entries = new List<HashManifestEntry>();
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            line = line.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int split = line.IndexOfAny(new[] { ' ', '\t' });
            if (split <= 0)
                continue;

            string sha1 = line[..split].Trim();
            string filePath = line[(split + 1)..].TrimStart();
            if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(filePath))
                continue;
            entries.Add(new HashManifestEntry(sha1.ToLowerInvariant(), filePath));
        }
        return entries;
    }

    private static async Task<IsoTree> ReadIsoTreeAsync(
        SkeletonImageReader image,
        CancellationToken cancellationToken)
    {
        byte[]? pvd = null;
        byte[]? jolietSvd = null;
        int descriptorCount = 0;
        for (int descriptor = 0; descriptor < 64; descriptor++)
        {
            byte[] sector = await image.ReadForm1SectorAsync(image.BaseLba + SystemAreaSectors + descriptor, cancellationToken);
            if (!sector.AsSpan(1, 5).SequenceEqual(Cd001))
                break;

            descriptorCount++;
            if (sector[0] == 1)
                pvd = sector;
            else if (sector[0] == 2 && sector.Length >= 91 &&
                     sector[88] == 0x25 && sector[89] == 0x2F &&
                     sector[90] is 0x40 or 0x43 or 0x45)
                jolietSvd ??= sector;
            if (sector[0] == 0xFF)
                break;
        }

        if (pvd is null)
            throw new InvalidOperationException("No ISO9660 primary volume descriptor was found in the skeleton.");

        string volumeIdentifier = Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd(' ', '\0');
        DirectoryRecord root = ParseDirectoryRecord(pvd, 156);
        var files = new List<IsoFileExtent>();
        var neroProjects = new List<NeroProjectEntry>();
        var visitedDirectories = new HashSet<(uint Lba, uint Length)>();
        var areaStarts = new HashSet<uint>();

        AddAreaStart(areaStarts, image.BaseLba);
        AddAreaStart(areaStarts, (long)image.BaseLba + SystemAreaSectors);
        AddAreaStart(areaStarts, (long)image.BaseLba + SystemAreaSectors + descriptorCount);

        uint pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(132, 4));
        if (pathTableSize > 0)
        {
            AddAreaStart(areaStarts, BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(140, 4)));
            AddAreaStart(areaStarts, BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(144, 4)));
            AddAreaStart(areaStarts, BinaryPrimitives.ReadUInt32BigEndian(pvd.AsSpan(148, 4)));
            AddAreaStart(areaStarts, BinaryPrimitives.ReadUInt32BigEndian(pvd.AsSpan(152, 4)));
        }

        AddAreaStart(areaStarts, root.Lba);
        await ReadDirectoryRecursiveAsync(
            image,
            root.Lba,
            root.DataLength,
            "/",
            files,
            neroProjects,
            visitedDirectories,
            areaStarts,
            cancellationToken);

        if (jolietSvd is not null && jolietSvd[156] >= 34)
        {
            DirectoryRecord jolietRoot = ParseDirectoryRecord(jolietSvd, 156);
            await ReadNeroDirectoryRecursiveAsync(
                image,
                jolietRoot.Lba,
                jolietRoot.DataLength,
                neroProjects,
                new HashSet<(uint Lba, uint Length)>(),
                cancellationToken).ConfigureAwait(false);
        }

        NeroProjectEntry[] distinctNeroProjects = neroProjects
            .GroupBy(project => (project.FileName, project.Lba, project.DataLength))
            .Select(group => group.First() with
            {
                DirectoryNamespaces = string.Join(
                    "+",
                    group.Select(project => project.DirectoryNamespaces)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
            })
            .ToArray();
        var verifiedNeroProjects = new List<NeroProjectEntry>();
        foreach (NeroProjectEntry candidate in distinctNeroProjects)
        {
            if (candidate.DataLength < 15 || candidate.Lba < image.BaseLba || candidate.Lba >= image.BaseLba + image.SectorCount)
                continue;

            try
            {
                uint bytesToRead = Math.Min(candidate.DataLength, 64u);
                byte[] header = await image.ReadForm1BytesAsync(candidate.Lba, bytesToRead, cancellationToken);
                if (!NeroNriDetector.TryReadPayloadSignature(header, out string signature))
                    continue;

                verifiedNeroProjects.Add(candidate with { Signature = signature });
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
            {
                // Keep the embedded record as a requirement. Inspection must be able to
                // warn about an unavailable NRI payload rather than failing the whole disc.
            }
        }

        uint volumeSpaceSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80, 4));
        long volumeEnd = volumeSpaceSize > image.SectorCount
            ? volumeSpaceSize
            : (long)image.BaseLba + volumeSpaceSize;
        AddAreaStart(areaStarts, volumeEnd);
        AddAreaStart(areaStarts, (long)image.BaseLba + image.SectorCount);

        return new IsoTree(
            volumeIdentifier,
            files,
            areaStarts.OrderBy(v => v).ToArray(),
            verifiedNeroProjects,
            distinctNeroProjects.Select(project => new NeroNriPayloadRequirement(
                project.FileName,
                project.Lba,
                project.DataLength,
                $"embedded {project.DirectoryNamespaces} directory record")).ToArray());
    }

    private static async Task ReadDirectoryRecursiveAsync(
        SkeletonImageReader image,
        uint directoryLba,
        uint directoryLength,
        string parentPath,
        List<IsoFileExtent> files,
        List<NeroProjectEntry> neroProjects,
        HashSet<(uint Lba, uint Length)> visited,
        HashSet<uint> areaStarts,
        CancellationToken cancellationToken)
    {
        if (!visited.Add((directoryLba, directoryLength)))
            return;

        AddAreaStart(areaStarts, directoryLba);
        byte[] data = await image.ReadForm1BytesAsync(directoryLba, directoryLength, cancellationToken);
        int offset = 0;
        while (offset < data.Length)
        {
            int recordLength = data[offset];
            if (recordLength == 0)
            {
                offset = ((offset / CookedSectorSize) + 1) * CookedSectorSize;
                continue;
            }
            if (offset + recordLength > data.Length || recordLength < 34)
                break;

            int recordOffset = offset;
            DirectoryRecord record = ParseDirectoryRecord(data, offset);
            offset += recordLength;

            if (NeroNriDetector.TryParseEmbeddedDirectoryRecord(
                    data.AsSpan(recordOffset, recordLength),
                    out NeroNriDetector.EmbeddedDirectoryRecord? neroRecord) &&
                neroRecord is not null)
            {
                neroProjects.Add(new NeroProjectEntry(
                    neroRecord.FileName,
                    neroRecord.ExtentLba,
                    neroRecord.DataLength,
                    string.Empty,
                    "ISO9660"));
            }

            if (record.Identifier.Length == 1 && (record.Identifier[0] == 0 || record.Identifier[0] == 1))
                continue;

            string identifier = Encoding.ASCII.GetString(record.Identifier);
            string name = StripIsoVersion(identifier);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string path = parentPath == "/" ? "/" + name : parentPath + "/" + name;
            bool isDirectory = (record.Flags & 0x02) != 0;
            if (isDirectory)
            {
                await ReadDirectoryRecursiveAsync(
                    image,
                    record.Lba,
                    record.DataLength,
                    path,
                    files,
                    neroProjects,
                    visited,
                    areaStarts,
                    cancellationToken);
            }
            else
            {
                AddAreaStart(areaStarts, record.Lba);

                // ISO9660 7.5.1 multi-extent files are represented by consecutive
                // directory records with the same identifier; bit 0x80 means another
                // extent follows.  Collapse those records into one logical file here so
                // every image-backed consumer hashes/materializes the host-visible file
                // rather than only the first physical extent.
                var logicalExtents = new List<SkeletonSourceImageExtent>
                {
                    new(record.Lba, record.DataLength)
                };
                long logicalLength = record.DataLength;
                byte[] logicalIdentifier = record.Identifier;
                byte logicalFlags = record.Flags;

                while ((logicalFlags & 0x80) != 0)
                {
                    // Multi-extent continuation records must immediately follow the
                    // preceding record in the same directory. Skip zero padding only
                    // to the next logical sector, exactly as the main parser does.
                    while (offset < data.Length && data[offset] == 0)
                        offset = ((offset / CookedSectorSize) + 1) * CookedSectorSize;

                    if (offset >= data.Length || data[offset] < 34 || offset + data[offset] > data.Length)
                        throw new InvalidOperationException($"ISO9660 multi-extent file is missing its continuation record: {path}");

                    DirectoryRecord continuation = ParseDirectoryRecord(data, offset);
                    offset += data[offset];
                    if (!continuation.Identifier.AsSpan().SequenceEqual(logicalIdentifier) || (continuation.Flags & 0x02) != 0)
                        throw new InvalidOperationException($"ISO9660 multi-extent continuation does not match {path}.");

                    AddAreaStart(areaStarts, continuation.Lba);
                    logicalExtents.Add(new SkeletonSourceImageExtent(continuation.Lba, continuation.DataLength));
                    logicalLength = checked(logicalLength + continuation.DataLength);
                    logicalFlags = continuation.Flags;
                }

                if (logicalLength > uint.MaxValue)
                    throw new InvalidOperationException($"ISO9660 logical file is too large for the current SkeleTool entry model: {path}");

                files.Add(new IsoFileExtent(path, record.Lba, checked((uint)logicalLength), logicalExtents));
            }
        }
    }

    private static async Task ReadNeroDirectoryRecursiveAsync(
        SkeletonImageReader image,
        uint directoryLba,
        uint directoryLength,
        List<NeroProjectEntry> neroProjects,
        HashSet<(uint Lba, uint Length)> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add((directoryLba, directoryLength)))
            return;

        byte[] data = await image.ReadForm1BytesAsync(directoryLba, directoryLength, cancellationToken).ConfigureAwait(false);
        int offset = 0;
        while (offset < data.Length)
        {
            int recordLength = data[offset];
            if (recordLength == 0)
            {
                offset = ((offset / CookedSectorSize) + 1) * CookedSectorSize;
                continue;
            }
            if (recordLength < 34 || offset + recordLength > data.Length)
                break;

            DirectoryRecord record = ParseDirectoryRecord(data, offset);
            if (NeroNriDetector.TryParseEmbeddedDirectoryRecord(
                    data.AsSpan(offset, recordLength),
                    out NeroNriDetector.EmbeddedDirectoryRecord? embedded) &&
                embedded is not null)
            {
                neroProjects.Add(new NeroProjectEntry(
                    embedded.FileName,
                    embedded.ExtentLba,
                    embedded.DataLength,
                    string.Empty,
                    "Joliet"));
            }

            offset += recordLength;
            bool dot = record.Identifier.Length == 1 && (record.Identifier[0] == 0 || record.Identifier[0] == 1);
            if (!dot && (record.Flags & 0x02) != 0)
            {
                await ReadNeroDirectoryRecursiveAsync(
                    image,
                    record.Lba,
                    record.DataLength,
                    neroProjects,
                    visited,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool TryParseEmbeddedNeroProjectDirectoryRecord(
        ReadOnlySpan<byte> outerRecord,
        out string fileName,
        out uint extentLba,
        out uint dataLength)
    {
        if (!NeroNriDetector.TryParseEmbeddedDirectoryRecord(outerRecord, out NeroNriDetector.EmbeddedDirectoryRecord? record) ||
            record is null)
        {
            fileName = string.Empty;
            extentLba = 0;
            dataLength = 0;
            return false;
        }

        fileName = record.FileName;
        extentLba = record.ExtentLba;
        dataLength = record.DataLength;
        return true;
    }

    private static bool IsNeroProjectFileName(string value) => NeroNriDetector.IsProjectFileName(value);

    private static void AddAreaStart(HashSet<uint> starts, long lba)
    {
        if (lba >= 0 && lba <= uint.MaxValue)
            starts.Add((uint)lba);
    }

    private static DirectoryRecord ParseDirectoryRecord(byte[] data, int offset)
    {
        int length = data[offset];
        if (length < 34 || offset + length > data.Length)
            throw new InvalidOperationException("Invalid ISO9660 directory record in skeleton.");

        uint lba = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 2, 4));
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 10, 4));
        byte flags = data[offset + 25];
        int identifierLength = data[offset + 32];
        if (offset + 33 + identifierLength > offset + length)
            throw new InvalidOperationException("Invalid ISO9660 directory identifier in skeleton.");
        byte[] identifier = data.AsSpan(offset + 33, identifierLength).ToArray();
        return new DirectoryRecord(lba, dataLength, flags, identifier);
    }

    private static string StripIsoVersion(string value)
    {
        int semicolon = value.LastIndexOf(';');
        if (semicolon > 0 && semicolon < value.Length - 1 && value[(semicolon + 1)..].All(char.IsDigit))
            return value[..semicolon];
        return value;
    }

    private static string NormalizeIsoPath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        return path;
    }

    private static string NormalizeManifestPath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        if (path.Equals("SYSTEM_AREA", StringComparison.OrdinalIgnoreCase) || path.StartsWith("GAP_", StringComparison.OrdinalIgnoreCase))
            return path;
        return NormalizeIsoPath(path);
    }

    private static bool TryParseGapLba(string path, out uint lba)
    {
        lba = 0;
        string value = path;
        if (value.EndsWith(".XA", StringComparison.OrdinalIgnoreCase))
            value = value[..^3];
        if (!value.StartsWith("GAP_", StringComparison.OrdinalIgnoreCase))
            return false;
        return uint.TryParse(value.AsSpan(4), out lba);
    }

    private static long DivideRoundUp(long value, long divisor) => (value + divisor - 1) / divisor;

    private static int DecodeBcd(byte value)
    {
        int hi = (value >> 4) & 0x0F;
        int lo = value & 0x0F;
        if (hi > 9 || lo > 9)
            throw new InvalidOperationException("Invalid BCD MSF address in raw skeleton.");
        return hi * 10 + lo;
    }

    private static void EnsureDifferentPaths(string a, string b)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(a).Equals(Path.GetFullPath(b), comparison))
            throw new InvalidOperationException("The output path must be different from the source skeleton.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup should not mask the original error.
        }
    }

    private static FileStream OpenRead(string path, int bufferSize, FileOptions options) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize,
        options);

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of file.");
            total += read;
        }
    }
}
