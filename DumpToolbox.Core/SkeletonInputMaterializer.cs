using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpCompress.Archives;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Readers;

namespace DumpToolbox.Core;

internal sealed record PreparedSkeletonInput(string Path, string Format, bool WasMaterialized);

/// <summary>
/// Turns old compressed skeletons and archive-contained skeletons into a seekable
/// working file. Resurrection performs random reads, so it cannot use a compressed
/// stream directly.
/// </summary>
internal static class SkeletonInputMaterializer
{
    private const int CopyBufferSize = 1024 * 1024;
    private const long ProgressInterval = 8L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, string> WorkingPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> GeneratedPaths = new(StringComparer.OrdinalIgnoreCase);

    static SkeletonInputMaterializer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (string path in GeneratedPaths.Keys)
            {
                TryDelete(path);
                TryDelete(path + ".partial");
            }
        };
    }

    public static async Task<PreparedSkeletonInput> PrepareAsync(
        string sourcePath,
        IProgress<SkeletonInputPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath);
        byte[] header = await ReadHeaderAsync(source, 512, cancellationToken).ConfigureAwait(false);

        if (TryReadLzmaAloneHeader(header, new FileInfo(source).Length, out long? lzmaLength))
        {
            string destination = GetDestination(source, null, lzmaLength);
            if (!IsCompleteCachedFile(destination, lzmaLength))
            {
                await MaterializeAsync(
                    destination,
                    "Decompressing legacy LZMA skeleton...",
                    lzmaLength,
                    async (output, ct) =>
                    {
                        await using var input = new FileStream(
                            source, FileMode.Open, FileAccess.Read, FileShare.Read,
                            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        byte[] lzmaHeader = new byte[13];
                        await input.ReadExactlyAsync(lzmaHeader, ct).ConfigureAwait(false);
                        long inputSize = input.Length - input.Position;
                        long outputSize = lzmaLength ?? -1;
                        await using LzmaStream decoder = LzmaStream.Create(
                            lzmaHeader.AsSpan(0, 5).ToArray(),
                            input,
                            inputSize,
                            outputSize,
                            leaveOpen: true);
                        return await CopyWithProgressAsync(
                            decoder, output, "Decompressing legacy LZMA skeleton...",
                            lzmaLength, progress, ct).ConfigureAwait(false);
                    },
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new SkeletonInputPreparationProgress(
                "Legacy LZMA skeleton ready", new FileInfo(destination).Length, lzmaLength));
            return new PreparedSkeletonInput(destination, "legacy raw LZMA skeleton", true);
        }

        if (IsZstandard(header))
        {
            string destination = GetDestination(source, null, null);
            if (!File.Exists(destination))
            {
                await MaterializeAsync(
                    destination,
                    "Decompressing Zstandard skeleton...",
                    null,
                    async (output, ct) =>
                    {
                        await using var input = new FileStream(
                            source, FileMode.Open, FileAccess.Read, FileShare.Read,
                            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var decoder = new DecompressionStream(
                            input, CopyBufferSize, checkEndOfStream: true, leaveOpen: true);
                        return await CopyWithProgressAsync(
                            decoder, output, "Decompressing Zstandard skeleton...",
                            null, progress, ct).ConfigureAwait(false);
                    },
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            long length = new FileInfo(destination).Length;
            progress?.Report(new SkeletonInputPreparationProgress("Zstandard skeleton ready", length, length));
            return new PreparedSkeletonInput(destination, "Zstandard-compressed skeleton", true);
        }

        if (IsSupportedArchive(header))
            return await ExtractSkeletonFromArchiveAsync(source, progress, cancellationToken).ConfigureAwait(false);

        return new PreparedSkeletonInput(source, "uncompressed skeleton", false);
    }

    private static async Task<PreparedSkeletonInput> ExtractSkeletonFromArchiveAsync(
        string archivePath,
        IProgress<SkeletonInputPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArchiveSkeletonEntry[] entries = ReadArchiveSkeletonEntries(archivePath);
        ArchiveSkeletonEntry selected = entries.Length switch
        {
            0 => throw new InvalidOperationException(
                $"'{Path.GetFileName(archivePath)}' is an archive, but it contains no .skeleton file."),
            1 => entries[0],
            _ => SelectNamedArchiveEntry(archivePath, entries)
        };

        string destination = GetDestination(archivePath, selected.Key, selected.Size);
        if (!IsCompleteCachedFile(destination, selected.Size))
        {
            await MaterializeAsync(
                destination,
                $"Extracting {Path.GetFileName(selected.Key)}...",
                selected.Size,
                (output, ct) => ExtractArchiveEntryAsync(
                    archivePath, selected.Key, selected.Size, output, progress, ct),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new SkeletonInputPreparationProgress(
            $"{Path.GetFileName(selected.Key)} ready", selected.Size, selected.Size));
        return new PreparedSkeletonInput(
            destination,
            $"archive-contained skeleton ({Path.GetFileName(selected.Key)})",
            true);
    }

    private static ArchiveSkeletonEntry[] ReadArchiveSkeletonEntries(string archivePath)
    {
        var entries = new List<ArchiveSkeletonEntry>();
        var info = ArchiveFactory.GetArchiveInformation(archivePath);
        if (info?.SupportsRandomAccess == true)
        {
            using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
            entries.AddRange(archive.Entries
                .Where(entry => !entry.IsDirectory &&
                                entry.Key is not null &&
                                entry.Key.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ArchiveSkeletonEntry(entry.Key!, checked((long)entry.Size))));
        }
        else
        {
            using var stream = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.SequentialScan);
            using IReader reader = ReaderFactory.OpenReader(stream);
            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory &&
                    reader.Entry.Key is { } key &&
                    key.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new ArchiveSkeletonEntry(key, checked((long)reader.Entry.Size)));
                }
            }
        }

        return entries.ToArray();
    }

    private static ArchiveSkeletonEntry SelectNamedArchiveEntry(
        string archivePath,
        IReadOnlyList<ArchiveSkeletonEntry> entries)
    {
        string archiveStem = Path.GetFileNameWithoutExtension(archivePath);
        ArchiveSkeletonEntry[] nameMatches = entries
            .Where(entry => Path.GetFileNameWithoutExtension(entry.Key)
                .Equals(archiveStem, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nameMatches.Length == 1)
            return nameMatches[0];

        string names = string.Join(", ", entries.Take(8).Select(entry => $"'{entry.Key}'"));
        throw new InvalidOperationException(
            $"'{Path.GetFileName(archivePath)}' contains more than one .skeleton file and no unique filename match: {names}. " +
            "Supply an archive containing one skeleton, or give the matching skeleton its archive's filename.");
    }

    private static async Task<long> ExtractArchiveEntryAsync(
        string archivePath,
        string entryKey,
        long expectedSize,
        Stream output,
        IProgress<SkeletonInputPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string message = $"Extracting {Path.GetFileName(entryKey)}...";
        var info = ArchiveFactory.GetArchiveInformation(archivePath);
        if (info?.SupportsRandomAccess == true)
        {
            using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
            var entry = archive.Entries.FirstOrDefault(candidate =>
                !candidate.IsDirectory && PathsEqual(candidate.Key, entryKey))
                ?? throw new FileNotFoundException($"Archive entry '{entryKey}' disappeared from '{archivePath}'.");
            using Stream input = entry.OpenEntryStream();
            return await CopyWithProgressAsync(
                input, output, message, expectedSize, progress, cancellationToken).ConfigureAwait(false);
        }

        await using var archiveStream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IReader reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory || !PathsEqual(reader.Entry.Key, entryKey))
                continue;

            using Stream input = reader.OpenEntryStream();
            return await CopyWithProgressAsync(
                input, output, message, expectedSize, progress, cancellationToken).ConfigureAwait(false);
        }

        throw new FileNotFoundException($"Archive entry '{entryKey}' disappeared from '{archivePath}'.");
    }

    private static async Task MaterializeAsync(
        string destination,
        string message,
        long? expectedSize,
        Func<Stream, CancellationToken, Task<long>> write,
        IProgress<SkeletonInputPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".partial";
        TryDelete(partial);
        progress?.Report(new SkeletonInputPreparationProgress(message, 0, expectedSize));
        try
        {
            await using (var output = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long written = await write(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (expectedSize is >= 0 && written != expectedSize.Value)
                    throw new InvalidDataException(
                        $"The expanded skeleton is {written:N0} bytes; {expectedSize.Value:N0} bytes were expected.");
            }

            File.Move(partial, destination, overwrite: true);
            GeneratedPaths.TryAdd(destination, 0);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task<long> CopyWithProgressAsync(
        Stream input,
        Stream output,
        string message,
        long? totalBytes,
        IProgress<SkeletonInputPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long written = 0;
        long nextProgress = ProgressInterval;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (written >= nextProgress)
            {
                progress?.Report(new SkeletonInputPreparationProgress(message, written, totalBytes));
                nextProgress = written + ProgressInterval;
            }
        }

        progress?.Report(new SkeletonInputPreparationProgress(message, written, totalBytes));
        return written;
    }

    private static async Task<byte[]> ReadHeaderAsync(
        string path,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int length = (int)Math.Min(stream.Length, maximumLength);
        byte[] header = new byte[length];
        if (length > 0)
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return header;
    }

    internal static bool TryReadLzmaAloneHeader(byte[] header, long compressedLength, out long? outputLength)
    {
        outputLength = null;
        if (header.Length < 13 || compressedLength <= 13 || header[0] > 224)
            return false;

        uint dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1, 4));
        if (!IsPlausibleLzmaDictionary(dictionarySize))
            return false;

        ulong declared = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(5, 8));
        if (declared == ulong.MaxValue)
            return true;
        if (declared == 0 || declared > long.MaxValue || declared <= (ulong)compressedLength)
            return false;

        outputLength = (long)declared;
        return true;
    }

    private static bool IsPlausibleLzmaDictionary(uint value)
    {
        if (value < 4096 || value > 1_073_741_824)
            return false;
        return (value & (value - 1)) == 0 ||
               (value % 3 == 0 && ((value / 3) & ((value / 3) - 1)) == 0);
    }

    private static bool IsZstandard(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD;

    private static bool IsSupportedArchive(ReadOnlySpan<byte> header)
    {
        bool zip = header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                   header[2] is 0x03 or 0x05 or 0x07 && header[3] is 0x04 or 0x06 or 0x08;
        bool sevenZip = header.Length >= 6 && header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        bool rar = header.Length >= 7 && header[..7].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }) ||
                   header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        bool gzip = header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B;
        bool bzip2 = header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 0x42, 0x5A, 0x68 });
        bool xz = header.Length >= 6 && header[..6].SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 });
        bool tar = header.Length >= 265 && header.Slice(257, 5).SequenceEqual("ustar"u8);
        return zip || sevenZip || rar || gzip || bzip2 || xz || tar;
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null && right is not null &&
        left.Replace('\\', '/').TrimStart('/')
            .Equals(right.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private static string GetDestination(string source, string? entryKey, long? expectedSize)
    {
        FileInfo info = new(source);
        string identity = $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{entryKey}\n{expectedSize}";
        if (WorkingPaths.TryGetValue(identity, out string? existing))
            return existing;

        string leaf;
        if (entryKey is not null)
        {
            leaf = Path.GetFileName(entryKey);
        }
        else if (info.Name.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
        {
            // Historical dumps sometimes put a raw LZMA stream directly in a file
            // already named *.skeleton, so the decompressed neighbour needs a suffix.
            leaf = info.Name + ".temp";
        }
        else
        {
            // A wrapper such as disc.skeleton.zst becomes disc.skeleton beside it.
            leaf = Path.GetFileNameWithoutExtension(info.Name);
            if (!leaf.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
                leaf += ".skeleton";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
            leaf = leaf.Replace(invalid, '_');

        string directory = info.DirectoryName ?? Directory.GetCurrentDirectory();
        string destination = Path.Combine(directory, leaf);
        if (File.Exists(destination) || File.Exists(destination + ".partial"))
            destination = FindAvailableTemporaryName(destination);

        return WorkingPaths.GetOrAdd(identity, destination);
    }

    private static bool IsCompleteCachedFile(string path, long? expectedSize) =>
        File.Exists(path) && (expectedSize is null || new FileInfo(path).Length == expectedSize.Value);

    private static string FindAvailableTemporaryName(string intendedPath)
    {
        string first = intendedPath.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
            ? intendedPath
            : intendedPath + ".temp";
        if (!File.Exists(first) && !File.Exists(first + ".partial"))
            return first;

        string withoutTemp = first[..^".temp".Length];
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{withoutTemp}.{suffix}.temp";
            if (!File.Exists(candidate) && !File.Exists(candidate + ".partial"))
                return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record ArchiveSkeletonEntry(string Key, long Size);
}
