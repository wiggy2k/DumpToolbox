using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed record Ps3IrdExtent(long FirstSector, long Length);
public sealed record Ps3IrdFileEntry(string Path, long FirstSector, long Length, string Md5, IReadOnlyList<Ps3IrdExtent> Extents)
{
    public int ExtentCount => Extents.Count;
    public bool IsMultiExtent => Extents.Count > 1;
}
public sealed record Ps3IrdRegion(int Index, uint StartSector, uint LengthSectors, bool IsPlain, string Md5)
{
    public uint EndSector => checked(StartSector + LengthSectors - 1);
}
public sealed record Ps3IrdInfo(
    int Version,
    string GameId,
    string GameName,
    string UpdateVersion,
    string GameVersion,
    string AppVersion,
    int BlockSize,
    ulong DiscSize,
    IReadOnlyList<Ps3IrdFileEntry> Files,
    IReadOnlyList<Ps3IrdRegion> Regions);
public sealed record Ps3IrdFileCheck(Ps3IrdFileEntry Entry, string? SourcePath, string Status, string? ActualMd5 = null);
public sealed record Ps3IrdVerificationResult(Ps3IrdInfo Ird, IReadOnlyList<Ps3IrdFileCheck> Files)
{
    public int Valid => Files.Count(x => x.Status == "OK");
    public int Missing => Files.Count(x => x.Status == "MISSING");
    public int Invalid => Files.Count(x => x.Status == "INVALID");
    public bool CanRebuild => Missing == 0 && Invalid == 0;
}
public sealed record Ps3IrdProgress(string Phase, int Current, int Total, string Message, double Percent);
public sealed record Ps3IrdRebuildResult(
    string OutputPath,
    int VerifiedRegions,
    int TotalRegions,
    bool RegionVerificationPassed,
    bool RegionVerificationPerformed);

public sealed class Ps3IrdRebuildService
{
    private const int DefaultBlockSize = 2048;

    public Ps3IrdInfo Inspect(string irdPath)
    {
        using IrdImage ird = IrdImage.Load(irdPath);
        return ird.ToInfo();
    }

    public async Task<Ps3IrdVerificationResult> VerifySourcesAsync(
        string irdPath,
        string sourceFolder,
        IProgress<Ps3IrdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

        using IrdImage ird = IrdImage.Load(irdPath);
        var entries = ird.Files.OrderBy(x => x.FirstSector).ToArray();
        var sourceIndex = BuildSourceIndex(sourceFolder);
        var checks = new List<Ps3IrdFileCheck>(entries.Length);

        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ps3IrdFileEntry entry = entries[i];
            string normalized = NormalizeRelative(entry.Path);
            string? source = FindSource(sourceIndex, normalized);

            if (source is null)
            {
                checks.Add(new(entry, null, "MISSING"));
                progress?.Report(new("Verify", i + 1, entries.Length, $"Missing: {entry.Path}", Percent(i + 1, entries.Length)));
                continue;
            }

            var fi = new FileInfo(source);
            if (fi.Length != entry.Length)
            {
                checks.Add(new(entry, source, "INVALID", $"size={fi.Length}"));
                progress?.Report(new("Verify", i + 1, entries.Length, $"Wrong size: {entry.Path}", Percent(i + 1, entries.Length)));
                continue;
            }

            string md5 = await ComputeMd5Async(source, cancellationToken).ConfigureAwait(false);
            string status = md5.Equals(entry.Md5, StringComparison.OrdinalIgnoreCase) ? "OK" : "INVALID";
            checks.Add(new(entry, source, status, md5));
            progress?.Report(new("Verify", i + 1, entries.Length, $"{status}: {entry.Path}", Percent(i + 1, entries.Length)));
        }

        return new(ird.ToInfo(), checks);
    }

    public Task<Ps3IrdRebuildResult> RebuildAsync(
        string irdPath,
        string sourceFolder,
        string outputPath,
        IProgress<Ps3IrdProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => RebuildInternalAsync(irdPath, sourceFolder, outputPath, null, progress, cancellationToken);

    public Task<Ps3IrdRebuildResult> RebuildVerifiedAsync(
        string irdPath,
        string sourceFolder,
        string outputPath,
        Ps3IrdVerificationResult verification,
        IProgress<Ps3IrdProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => RebuildInternalAsync(irdPath, sourceFolder, outputPath, verification, progress, cancellationToken);

    private async Task<Ps3IrdRebuildResult> RebuildInternalAsync(
        string irdPath,
        string sourceFolder,
        string outputPath,
        Ps3IrdVerificationResult? verification,
        IProgress<Ps3IrdProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (verification is null)
            verification = await VerifySourcesAsync(irdPath, sourceFolder, progress, cancellationToken).ConfigureAwait(false);
        else
            progress?.Report(new("Rebuild", 0, verification.Files.Count, "Using previously verified source files.", 0));

        if (!verification.CanRebuild)
            throw new InvalidOperationException($"Source verification failed: {verification.Missing} missing, {verification.Invalid} invalid file(s).");

        using IrdImage ird = IrdImage.Load(irdPath);
        string finalOutputPath = Path.GetFullPath(outputPath);
        string partialOutputPath = CreatePartialOutputPath(finalOutputPath);
        string? parent = Path.GetDirectoryName(finalOutputPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        try
        {
            await using (var output = new FileStream(partialOutputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ird.Header.Position = 0;
                await ird.Header.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                var checks = verification.Files.OrderBy(x => x.Entry.FirstSector).ToArray();
                byte[] buffer = new byte[1024 * 1024];
                for (int i = 0; i < checks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Ps3IrdFileCheck check = checks[i];
                    string source = check.SourcePath!;
                    progress?.Report(new("Rebuild", i + 1, checks.Length, $"Writing: {check.Entry.Path}", Percent(i, checks.Length)));

                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    foreach (Ps3IrdExtent extent in check.Entry.Extents)
                    {
                        output.Position = checked(extent.FirstSector * (long)ird.BlockSize);
                        long remaining = extent.Length;
                        while (remaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int wanted = (int)Math.Min(buffer.Length, remaining);
                            int read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                            if (read <= 0)
                                throw new EndOfStreamException($"Unexpected end of source file: {source}");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            remaining -= read;
                        }
                    }

                    if (input.Position != check.Entry.Length)
                        throw new InvalidDataException($"Source file length changed while rebuilding: {source}");
                }

                output.Position = output.Length;
                ird.Footer.Position = 0;
                await ird.Footer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                if ((ulong)output.Length != ird.DiscSize)
                    throw new InvalidDataException($"Rebuilt ISO size is {output.Length:N0} bytes; IRD expects {ird.DiscSize:N0} bytes.");
            }

            File.Move(partialOutputPath, finalOutputPath, overwrite: true);
        }
        catch
        {
            DeleteFileQuietly(partialOutputPath);
            throw;
        }

        // The JB-folder rebuild produced here is the plain/decrypted PS3 ISO used as the
        // input to a later disc-key encryption stage. IRD region hashes describe the disc
        // region representation and must not be treated as a fatal checksum for this plain
        // intermediate image. They become meaningful when validating the encrypted image.
        progress?.Report(new("Rebuild", verification.Files.Count, verification.Files.Count,
            "Plain ISO complete; IRD region-hash verification is deferred until encryption.", 100));

        return new(finalOutputPath, 0, ird.Regions.Count, false, false);
    }


    public byte[] ResolveDiscKey(string? keyFilePath, string? keyText)
    {
        if (!string.IsNullOrWhiteSpace(keyText))
            return ParseDiscKeyText(keyText);

        if (string.IsNullOrWhiteSpace(keyFilePath))
            throw new InvalidOperationException("Supply a PS3 disc key as 32 hexadecimal characters or choose a .key/.txt file.");
        if (!File.Exists(keyFilePath))
            throw new FileNotFoundException("Disc key file not found.", keyFilePath);

        byte[] raw = File.ReadAllBytes(keyFilePath);
        if (raw.Length == 16)
            return raw;

        string text = Encoding.ASCII.GetString(raw);
        return ParseDiscKeyText(text);
    }

    public async Task<Ps3IrdRebuildResult> EncryptIsoAsync(
        string irdPath,
        string plainIsoPath,
        string encryptedOutputPath,
        byte[] discKey,
        IProgress<Ps3IrdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (discKey is null || discKey.Length != 16)
            throw new ArgumentException("PS3 disc key must be exactly 16 bytes (32 hexadecimal characters).", nameof(discKey));
        if (!File.Exists(plainIsoPath))
            throw new FileNotFoundException("Plain rebuilt ISO not found.", plainIsoPath);

        using IrdImage ird = IrdImage.Load(irdPath);
        if (ird.Regions.Count == 0)
            throw new InvalidDataException("IRD does not contain a usable PS3 region map; encrypted reconstruction cannot be performed safely.");
        var plainInfo = new FileInfo(plainIsoPath);
        if ((ulong)plainInfo.Length != ird.DiscSize)
            throw new InvalidDataException($"Plain ISO size is {plainInfo.Length:N0} bytes; IRD expects {ird.DiscSize:N0} bytes.");

        string finalOutputPath = Path.GetFullPath(encryptedOutputPath);
        string partialOutputPath = CreatePartialOutputPath(finalOutputPath);
        string? parent = Path.GetDirectoryName(finalOutputPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        const int sectorsPerChunk = 512;
        int sectorSize = ird.BlockSize;
        byte[] inputBuffer = new byte[sectorsPerChunk * sectorSize];
        byte[] outputBuffer = new byte[inputBuffer.Length];
        long totalSectors = (long)(ird.DiscSize / (ulong)sectorSize);
        long completedSectors = 0;

        int verified;
        try
        {
            await using var input = new FileStream(plainIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, inputBuffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(partialOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, outputBuffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (Ps3IrdRegion region in ird.Regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    input.Position = checked((long)region.StartSector * sectorSize);
                    output.Position = input.Position;
                    long remaining = region.LengthSectors;
                    progress?.Report(new("Encrypt", (int)Math.Min(completedSectors, int.MaxValue), (int)Math.Min(totalSectors, int.MaxValue),
                        $"{(region.IsPlain ? "Copying" : "Encrypting")} region {region.Index + 1}/{ird.Regions.Count}: sectors {region.StartSector:N0}-{region.EndSector:N0}",
                        totalSectors == 0 ? 0 : completedSectors * 100.0 / totalSectors));

                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int sectors = (int)Math.Min(sectorsPerChunk, remaining);
                        int bytes = checked(sectors * sectorSize);
                        await ReadExactlyAsync(input, inputBuffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);

                        if (region.IsPlain)
                        {
                            Buffer.BlockCopy(inputBuffer, 0, outputBuffer, 0, bytes);
                        }
                        else
                        {
                            uint firstLba = checked((uint)(region.StartSector + (region.LengthSectors - (uint)remaining)));
                            var options = new ParallelOptions
                            {
                                CancellationToken = cancellationToken,
                                MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
                            };
                            Parallel.For(0, sectors, options,
                                () =>
                                {
                                    Aes aes = Aes.Create();
                                    aes.Key = discKey;
                                    aes.Mode = CipherMode.CBC;
                                    aes.Padding = PaddingMode.None;
                                    return aes;
                                },
                                (sectorIndex, _, aes) =>
                                {
                                    Span<byte> iv = stackalloc byte[16];
                                    BinaryPrimitives.WriteUInt32BigEndian(iv[12..], checked(firstLba + (uint)sectorIndex));
                                    int offset = checked(sectorIndex * sectorSize);
                                    aes.EncryptCbc(inputBuffer.AsSpan(offset, sectorSize), iv, outputBuffer.AsSpan(offset, sectorSize), PaddingMode.None);
                                    return aes;
                                },
                                aes => aes.Dispose());
                        }

                        await output.WriteAsync(outputBuffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                        remaining -= sectors;
                        completedSectors += sectors;

                        // Report every chunk. Progress<T> marshals this back to the Avalonia UI
                        // thread while the encryption itself remains on the worker thread.
                        double percent = totalSectors == 0 ? 0 : completedSectors * 98.0 / totalSectors;
                        progress?.Report(new(
                            "Encrypt",
                            (int)Math.Min(completedSectors, int.MaxValue),
                            (int)Math.Min(totalSectors, int.MaxValue),
                            $"{(region.IsPlain ? "Copying" : "Encrypting")} region {region.Index + 1}/{ird.Regions.Count}: sectors {region.StartSector:N0}-{region.EndSector:N0}",
                            percent));
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new("Verify ISO", 0, ird.Regions.Count, "Verifying encrypted ISO against IRD region hashes...", 99));
            verified = await VerifyRegionsAsync(partialOutputPath, ird, progress, cancellationToken).ConfigureAwait(false);
            if (verified != ird.Regions.Count)
                throw new InvalidDataException($"Encrypted ISO IRD region verification failed ({verified}/{ird.Regions.Count} regions matched). Check that the supplied value is the 16-byte disc key for this disc.");

            File.Move(partialOutputPath, finalOutputPath, overwrite: true);
        }
        catch
        {
            DeleteFileQuietly(partialOutputPath);
            throw;
        }

        progress?.Report(new("Encrypt", (int)Math.Min(totalSectors, int.MaxValue), (int)Math.Min(totalSectors, int.MaxValue),
            $"Encrypted ISO complete; {verified}/{ird.Regions.Count} IRD regions verified.", 100));
        return new(finalOutputPath, verified, ird.Regions.Count, true, true);
    }

    private static string CreatePartialOutputPath(string finalOutputPath)
        => finalOutputPath + $".{Guid.NewGuid():N}.partial";

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup must not hide the original rebuild/encryption failure.
        }
    }

    private static byte[] ParseDiscKeyText(string value)
    {
        string hex = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        if (hex.Length != 32 || hex.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("PS3 disc key must contain exactly 32 hexadecimal characters (16 bytes).");
        return Convert.FromHexString(hex);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of PS3 ISO while encrypting.");
            offset += read;
        }
    }

    public string SuggestOutputPath(string irdPath)
    {
        using IrdImage ird = IrdImage.Load(irdPath);
        string name = string.Join("_", $"{ird.GameId}-{ird.GameName}".Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(name)) name = "rebuilt_ps3";
        return name + ".iso";
    }

    private static Dictionary<string, string> BuildSourceIndex(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = NormalizeRelative(Path.GetRelativePath(root, path));
            map.TryAdd(rel, path);
        }
        return map;
    }

    private static string? FindSource(Dictionary<string, string> index, string normalized)
    {
        if (index.TryGetValue(normalized, out string? exact)) return exact;
        if (normalized.EndsWith(";1", StringComparison.OrdinalIgnoreCase) && index.TryGetValue(normalized[..^2], out string? noVersion)) return noVersion;
        return null;
    }

    private static string NormalizeRelative(string value)
    {
        string s = value.Replace('\\', '/').TrimStart('/');
        return s.EndsWith(";1", StringComparison.OrdinalIgnoreCase) ? s[..^2] : s;
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken token)
    {
        using MD5 md5 = MD5.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> VerifyRegionsAsync(string isoPath, IrdImage ird, IProgress<Ps3IrdProgress>? progress, CancellationToken token)
    {
        // IRD RegionHashes are not hashes of the complete crypt-map regions.  When an
        // IRD is generated, its separately stored header is removed from region 0 and
        // its separately stored footer is removed from the final region before those
        // MD5s are calculated.  See LibIRD GetRegions()/HashISO().
        long headerSectors = ird.Header.Length / ird.BlockSize;
        long footerStartByte = checked((long)ird.DiscSize - ird.Footer.Length);
        long footerStartSector = footerStartByte / ird.BlockSize;

        int verified = 0;
        await using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 1024];
        for (int i = 0; i < ird.Regions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Ps3IrdRegion region = ird.Regions[i];

            long hashStartSector = region.StartSector;
            long hashEndSector = region.EndSector;
            if (i == 0)
                hashStartSector = Math.Max(hashStartSector, headerSectors);
            if (i == ird.Regions.Count - 1)
                hashEndSector = Math.Min(hashEndSector, footerStartSector - 1);

            if (hashEndSector < hashStartSector)
                throw new InvalidDataException($"IRD region {i + 1} has no hashable sectors after excluding the IRD header/footer.");

            stream.Position = checked(hashStartSector * ird.BlockSize);
            long remaining = checked((hashEndSector - hashStartSector + 1) * ird.BlockSize);
            using MD5 md5 = MD5.Create();
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
                if (read <= 0)
                    throw new EndOfStreamException($"Unexpected end of ISO while verifying IRD region {i + 1}.");
                md5.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string actual = Convert.ToHexString(md5.Hash!).ToLowerInvariant();
            bool match = actual.Equals(region.Md5, StringComparison.OrdinalIgnoreCase);
            if (match) verified++;

            progress?.Report(new("Verify ISO", i + 1, ird.Regions.Count,
                $"Region {i + 1}/{ird.Regions.Count} {(match ? "OK" : "MISMATCH")}: sectors {hashStartSector:N0}-{hashEndSector:N0}; expected {region.Md5}; actual {actual}",
                Percent(i + 1, ird.Regions.Count)));
        }
        return verified;
    }

    private static double Percent(int current, int total) => total <= 0 ? 0 : Math.Clamp(current * 100.0 / total, 0, 100);

    private sealed class IrdImage : IDisposable
    {
        public int Version { get; private set; }
        public string GameId { get; private set; } = "";
        public string GameName { get; private set; } = "";
        public string UpdateVersion { get; private set; } = "";
        public string GameVersion { get; private set; } = "";
        public string AppVersion { get; private set; } = "";
        public MemoryStream Header { get; private set; } = new();
        public MemoryStream Footer { get; private set; } = new();
        public int BlockSize { get; private set; } = DefaultBlockSize;
        public ulong DiscSize { get; private set; }
        public List<Ps3IrdFileEntry> Files { get; } = new();
        public List<Ps3IrdRegion> Regions { get; } = new();
        private Dictionary<long, byte[]> FileHashes { get; } = new();
        private List<byte[]> RegionHashes { get; } = new();

        public static IrdImage Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("IRD file not found.", path);
            byte[] raw = File.ReadAllBytes(path);
            byte[] data = IsMagic(raw) ? raw : Gunzip(raw);
            var result = new IrdImage();
            result.Parse(data);
            return result;
        }

        private void Parse(byte[] data)
        {
            using var br = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8, leaveOpen: false);
            byte[] magic = br.ReadBytes(4);
            if (!IsMagic(magic)) throw new InvalidDataException("Not a valid PS3 IRD (3IRD magic missing).");
            Version = br.ReadByte();
            if (Version is < 6 or > 9) throw new NotSupportedException($"IRD version {Version} is not supported (supported: 6-9).");
            GameId = Encoding.ASCII.GetString(br.ReadBytes(9)).TrimEnd('\0', ' ');
            GameName = br.ReadString();
            UpdateVersion = Encoding.ASCII.GetString(br.ReadBytes(4)).TrimEnd('\0', ' ');
            GameVersion = Encoding.ASCII.GetString(br.ReadBytes(5)).TrimEnd('\0', ' ');
            AppVersion = Encoding.ASCII.GetString(br.ReadBytes(5)).TrimEnd('\0', ' ');
            if (Version == 7) br.ReadBytes(4);
            Header = ReadCompressedBlock(br);
            Footer = ReadCompressedBlock(br);

            int regionCount = br.ReadByte();
            for (int i = 0; i < regionCount; i++) RegionHashes.Add(br.ReadBytes(16));
            int fileCount = br.ReadInt32();
            if (fileCount < 0 || fileCount > 1_000_000) throw new InvalidDataException("IRD file count is invalid.");
            for (int i = 0; i < fileCount; i++) FileHashes[br.ReadInt64()] = br.ReadBytes(16);
            br.ReadUInt16();
            br.ReadUInt16();
            if (Version >= 9) br.ReadBytes(115); // PIC; retained by the IRD but not needed for a plain rebuild.
            br.ReadBytes(16); // Data1
            br.ReadBytes(16); // Data2
            if (Version < 9) br.ReadBytes(115);
            if (Version > 7) br.ReadUInt32();
            // trailing CRC32 is intentionally not required for rebuild; malformed/truncated data is rejected by reads above.

            ParseIsoHeader();
            ParseRegions();
        }

        private void ParseIsoHeader()
        {
            byte[] bytes = Header.ToArray();
            int pvdOffset = FindPvd(bytes);
            if (pvdOffset < 0) throw new InvalidDataException("IRD embedded header does not contain an ISO9660 PVD.");
            BlockSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pvdOffset + 128, 2));
            if (BlockSize <= 0) BlockSize = DefaultBlockSize;
            uint volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pvdOffset + 80, 4));
            DiscSize = (ulong)volumeSpace * (ulong)BlockSize;

            int rootOffset = pvdOffset + 156;
            IsoDirRecord root = ParseDirRecord(bytes, rootOffset, "");
            var visited = new HashSet<uint>();
            ParseDirectory(bytes, root.Extent, root.Length, "", visited);
        }

        private void ParseDirectory(byte[] bytes, uint extent, uint length, string parent, HashSet<uint> visited)
        {
            if (!visited.Add(extent)) return;
            long startL = (long)extent * BlockSize;
            if (startL < 0 || startL >= bytes.Length) return;
            int start = (int)startL;
            int end = (int)Math.Min(bytes.Length, startL + length);

            var records = new List<IsoDirRecord>();
            int pos = start;
            while (pos < end)
            {
                int len = bytes[pos];
                if (len == 0)
                {
                    pos = ((pos / BlockSize) + 1) * BlockSize;
                    continue;
                }
                if (pos + len > bytes.Length || len < 34) break;
                records.Add(ParseDirRecord(bytes, pos, parent));
                pos += len;
            }

            for (int i = 0; i < records.Count; i++)
            {
                IsoDirRecord rec = records[i];
                if (rec.Special) continue;
                string path = string.IsNullOrEmpty(parent) ? rec.Name : parent + "/" + rec.Name;

                if (rec.IsDirectory)
                {
                    ParseDirectory(bytes, rec.Extent, rec.Length, path, visited);
                    continue;
                }

                var extents = new List<Ps3IrdExtent> { new(rec.Extent, rec.Length) };
                long totalLength = rec.Length;
                IsoDirRecord current = rec;

                while (current.IsMultiExtent)
                {
                    if (i + 1 >= records.Count)
                        throw new InvalidDataException($"ISO9660 multi-extent file is missing its continuation record: {path}");

                    IsoDirRecord next = records[++i];
                    if (next.Special || next.IsDirectory || !next.Name.Equals(rec.Name, StringComparison.Ordinal))
                        throw new InvalidDataException($"ISO9660 multi-extent continuation does not match {path}.");

                    extents.Add(new(next.Extent, next.Length));
                    totalLength = checked(totalLength + next.Length);
                    current = next;
                }

                if (!FileHashes.TryGetValue(rec.Extent, out byte[]? md5)) continue;
                Files.Add(new(path, rec.Extent, totalLength, Convert.ToHexString(md5).ToLowerInvariant(), extents.ToArray()));
            }
        }

        private void ParseRegions()
        {
            byte[] bytes = Header.ToArray();
            if (bytes.Length < 12 || RegionHashes.Count == 0) return;
            uint first = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
            long countL = (long)first * 2 - 1;
            if (countL <= 0 || countL > RegionHashes.Count || 12 + countL * 4 > bytes.Length) return;
            int count = (int)countL;
            uint start = 0;
            int off = 12;
            for (int i = 0; i < count; i++, off += 4)
            {
                uint boundary = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(off, 4));
                bool plain = (i & 1) == 0;

                // Sector 0 stores alternating plain/encrypted boundaries. Plain-region
                // boundaries are inclusive; encrypted-region boundaries identify the first
                // sector of the following plain region, so their final sector is boundary-1.
                uint end = plain ? boundary : checked(boundary - 1);
                if (end < start) break;
                uint length = checked(end - start + 1);
                Regions.Add(new(i, start, length, plain, Convert.ToHexString(RegionHashes[i]).ToLowerInvariant()));
                start = checked(end + 1);
            }
        }

        public Ps3IrdInfo ToInfo() => new(Version, GameId, GameName, UpdateVersion, GameVersion, AppVersion, BlockSize, DiscSize, Files.ToArray(), Regions.ToArray());

        private static MemoryStream ReadCompressedBlock(BinaryReader br)
        {
            uint compressedLength = br.ReadUInt32();
            if (compressedLength > int.MaxValue) throw new InvalidDataException("IRD compressed block is too large.");
            byte[] compressed = br.ReadBytes((int)compressedLength);
            if (compressed.Length != compressedLength) throw new EndOfStreamException("IRD compressed block is truncated.");
            using var gz = new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
            var ms = new MemoryStream();
            gz.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        private static bool IsMagic(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == (byte)'3' && b[1] == (byte)'I' && b[2] == (byte)'R' && b[3] == (byte)'D';
        private static byte[] Gunzip(byte[] data)
        {
            using var gz = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return ms.ToArray();
        }

        private static int FindPvd(byte[] bytes)
        {
            const int sector = 2048;
            for (int s = 16; s < Math.Min(bytes.Length / sector, 512); s++)
            {
                int o = s * sector;
                if (o + sector > bytes.Length) break;
                if (bytes[o] == 1 && Encoding.ASCII.GetString(bytes, o + 1, 5) == "CD001") return o;
            }
            return -1;
        }

        private static IsoDirRecord ParseDirRecord(byte[] bytes, int offset, string parent)
        {
            int len = bytes[offset];
            uint extent = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 2, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 10, 4));
            byte flags = bytes[offset + 25];
            int nameLen = bytes[offset + 32];
            string name;
            bool special = false;
            if (nameLen == 1 && bytes[offset + 33] is 0 or 1)
            {
                special = true;
                name = bytes[offset + 33] == 0 ? "." : "..";
            }
            else
            {
                name = Encoding.ASCII.GetString(bytes, offset + 33, Math.Min(nameLen, len - 33));
                if (name.EndsWith(";1", StringComparison.Ordinal)) name = name[..^2];
            }
            return new(name, extent, length, (flags & 0x02) != 0, (flags & 0x80) != 0, special);
        }

        public void Dispose()
        {
            Header.Dispose();
            Footer.Dispose();
        }

        private sealed record IsoDirRecord(string Name, uint Extent, uint Length, bool IsDirectory, bool IsMultiExtent, bool Special);
    }
}
