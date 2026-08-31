using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed class HashSearchEngine
{
    private const int IoBufferSize = 4 * 1024 * 1024;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string filePath,
        IReadOnlyList<HashTarget> targets,
        int alignment = 2352,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file is required.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Input file not found.", filePath);
        if (alignment <= 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be at least 1 byte.");
        if (targets.Count == 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        // Keep all heavy synchronous file/CRC work off the Avalonia UI thread. Synchronous
        // sequential I/O is deliberately used inside the worker: tiny ReadAsync calls were a
        // major source of overhead in the first embedded implementation.
        return Task.Run<IReadOnlyList<SearchResult>>(
            () => SearchCore(filePath, targets, alignment, progress, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<SearchResult> SearchCore(
        string filePath,
        IReadOnlyList<HashTarget> targets,
        int alignment,
        IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        long fileLength = new FileInfo(filePath).Length;
        var resultByIndex = new SearchResult?[targets.Count];
        var searchable = new List<(HashTarget Target, int Index)>();

        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].Size <= 0)
                resultByIndex[i] = new SearchResult(targets[i], null, false, "Target size must be greater than zero.");
            else if (targets[i].Size > fileLength)
                resultByIndex[i] = new SearchResult(targets[i], null, false, "Target is larger than the input file.");
            else
                searchable.Add((targets[i], i));
        }

        // Search targets in pasted order. Once a target is found, the next target begins at the
        // byte immediately after that match. If it is not found before EOF, the scan wraps once
        // to offset zero and continues only up to the original start point. This mirrors how
        // consecutive disc tracks normally appear while still covering the whole input file.
        SearchSequentialTargets(filePath, fileLength, searchable, alignment, resultByIndex, targets.Count, progress, cancellationToken);

        var results = new SearchResult[targets.Count];
        for (int i = 0; i < targets.Count; i++)
            results[i] = resultByIndex[i] ?? new SearchResult(targets[i], null, false, "Not found.");
        return results;
    }


    private static void SearchSequentialTargets(
        string filePath,
        long fileLength,
        List<(HashTarget Target, int Index)> items,
        int alignment,
        SearchResult?[] resultByIndex,
        int totalTargets,
        IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        long nextStart = 0;
        long crcCandidates = 0;
        int completed = totalTargets - items.Count;

        foreach (var item in items.OrderBy(x => x.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashTarget target = item.Target;
            long lastOffset = fileLength - target.Size;

            // Start at (or, for aligned mode, immediately after) the end of the previous match.
            // If there is no room for this target before EOF, skip the tail and wrap straight to zero.
            long preferredStart = Math.Max(0, nextStart);
            if (alignment > 1 && preferredStart % alignment != 0)
                preferredStart += alignment - (preferredStart % alignment);
            bool hasTail = preferredStart <= lastOffset;

            progress?.Report(new SearchProgress(
                completed, totalTargets, target, hasTail ? preferredStart : 0, fileLength, crcCandidates,
                hasTail
                    ? $"Searching {(target.Label ?? target.Crc32Hex)} from offset {preferredStart:N0} (0x{preferredStart:X})"
                    : $"Previous match ends too near EOF; wrapping {(target.Label ?? target.Crc32Hex)} directly to offset 0",
                SearchEventKind.Progress));

            long? found = null;

            // Disc tracks are very often stored back-to-back. Before starting the rolling
            // scanner, hash the exact expected next position once. This turns long runs of
            // consecutive tracks into one direct read/hash per track.
            if (hasTail && preferredStart == nextStart && nextStart > 0)
            {
                progress?.Report(new SearchProgress(
                    completed, totalTargets, target, preferredStart, fileLength, crcCandidates,
                    $"Testing expected consecutive offset {preferredStart:N0} (0x{preferredStart:X})",
                    SearchEventKind.Progress));

                if (VerifyExactCandidate(filePath, target, preferredStart, cancellationToken, out string? actualMd5))
                {
                    crcCandidates++;
                    found = preferredStart;
                    progress?.Report(new SearchProgress(
                        completed, totalTargets, target, preferredStart, fileLength, crcCandidates,
                        $"Expected consecutive position matched at {preferredStart:N0} (0x{preferredStart:X})",
                        SearchEventKind.CrcCandidate, preferredStart, actualMd5));
                }
            }

            if (found is null && hasTail)
            {
                found = alignment == 1
                    ? SearchSingleByteTargetRange(filePath, fileLength, target, preferredStart, lastOffset, ref crcCandidates,
                        completed, totalTargets, progress, cancellationToken)
                    : SearchSingleAlignedTargetRange(filePath, fileLength, target, preferredStart, lastOffset, alignment, ref crcCandidates,
                        completed, totalTargets, progress, cancellationToken);
            }

            if (found is null && preferredStart > 0)
            {
                long wrapEnd = hasTail ? Math.Min(lastOffset, preferredStart - alignment) : lastOffset;
                if (wrapEnd >= 0)
                {
                    progress?.Report(new SearchProgress(
                        completed, totalTargets, target, 0, fileLength, crcCandidates,
                        $"Reached EOF without a match; wrapping to offset 0 and searching to {wrapEnd:N0}",
                        SearchEventKind.Progress));
                    found = alignment == 1
                        ? SearchSingleByteTargetRange(filePath, fileLength, target, 0, wrapEnd, ref crcCandidates,
                            completed, totalTargets, progress, cancellationToken)
                        : SearchSingleAlignedTargetRange(filePath, fileLength, target, 0, wrapEnd, alignment, ref crcCandidates,
                            completed, totalTargets, progress, cancellationToken);
                }
            }

            if (found is long offset)
            {
                progress?.Report(new SearchProgress(
                    completed, totalTargets, target, offset, fileLength, crcCandidates,
                    $"Hash verified at offset {offset:N0} (0x{offset:X}); extracting match...",
                    SearchEventKind.Progress, offset, target.NormalizedMd5));

                string outputPath = ExtractMatch(filePath, target, offset, cancellationToken);
                string status = (target.NormalizedMd5 is null ? "CRC32 match" : "CRC32 + MD5 match") +
                                $"; extracted to {Path.GetFileName(outputPath)}";
                resultByIndex[item.Index] = new SearchResult(target, offset, true, status, crcCandidates, outputPath);
                nextStart = offset + target.Size;
                completed++;
                progress?.Report(new SearchProgress(
                    completed, totalTargets, target, offset, fileLength, crcCandidates,
                    $"MATCH FOUND at offset {offset:N0} (0x{offset:X}); next target will start at {nextStart:N0} (0x{nextStart:X})",
                    SearchEventKind.MatchFound, offset, target.NormalizedMd5, outputPath));
                progress?.Report(new SearchProgress(
                    completed, totalTargets, target, offset, fileLength, crcCandidates,
                    $"Extracted {target.Size:N0} bytes to {outputPath}",
                    SearchEventKind.Extracted, offset, target.NormalizedMd5, outputPath));
            }
            else
            {
                resultByIndex[item.Index] = new SearchResult(target, null, false, "Not found.", crcCandidates);
                completed++;
                // A miss must not destroy the positional hint from the last successful target.
                progress?.Report(new SearchProgress(
                    completed, totalTargets, target, fileLength, fileLength, crcCandidates,
                    $"Not found after a complete wrapped scan; keeping next search start at {nextStart:N0}",
                    SearchEventKind.Progress));
            }
        }
    }

    private static long? SearchSingleByteTargetRange(
        string filePath, long fileLength, HashTarget target, long rangeStart, long rangeEnd,
        ref long crcCandidates, int completed, int totalTargets, IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (rangeStart > rangeEnd)
            return null;
        if (target.Size > int.MaxValue)
            throw new NotSupportedException("Byte-aligned targets larger than 2 GiB are not supported yet.");

        int windowLength = checked((int)target.Size);
        byte[] ring = ArrayPool<byte>.Shared.Rent(windowLength);
        byte[] io = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        var shiftTail = Crc32.CreateShiftOperator(target.Size - 1);
        var shiftOne = Crc32.CreateShiftOperator(1);
        uint[][] shiftOneTables = shiftOne.CreateByteTables();
        var outgoingContribution = new uint[256];
        var oneByteCrc = new uint[256];
        Span<byte> single = stackalloc byte[1];
        for (int b = 0; b < 256; b++)
        {
            single[0] = (byte)b;
            uint crc = Crc32.Compute(single);
            oneByteCrc[b] = crc;
            outgoingContribution[b] = shiftTail.Apply(crc);
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBufferSize, FileOptions.RandomAccess);
            stream.Position = rangeStart;
            ReadExactly(stream, ring.AsSpan(0, windowLength));
            uint currentCrc = Crc32.Compute(ring.AsSpan(0, windowLength));
            long offset = rangeStart;
            int ringPos = 0;
            long nextProgress = rangeStart;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (currentCrc == target.Crc32)
                {
                    crcCandidates++;
                    progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                        $"CRC32 candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.CrcCandidate, offset));
                    string? actualMd5 = target.NormalizedMd5 is null ? null : ComputeMd5(filePath, offset, target.Size, cancellationToken);
                    if (target.NormalizedMd5 is null || target.NormalizedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase))
                        return offset;
                    progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                        $"MD5 rejected CRC candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.Md5Rejected, offset, actualMd5));
                }

                if (offset >= rangeEnd)
                    break;

                int want = (int)Math.Min(io.Length, rangeEnd - offset);
                int got = stream.Read(io, 0, want);
                if (got == 0)
                    break;

                for (int i = 0; i < got; i++)
                {
                    byte outgoing = ring[ringPos];
                    byte incoming = io[i];
                    ring[ringPos] = incoming;
                    if (++ringPos == windowLength) ringPos = 0;
                    uint remainder = currentCrc ^ outgoingContribution[outgoing];
                    currentCrc = Crc32.ShiftOperator.ApplyByteTables(shiftOneTables, remainder) ^ oneByteCrc[incoming];
                    offset++;

                    if (currentCrc == target.Crc32)
                    {
                        crcCandidates++;
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"CRC32 candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.CrcCandidate, offset));
                        string? actualMd5 = target.NormalizedMd5 is null ? null : ComputeMd5(filePath, offset, target.Size, cancellationToken);
                        if (target.NormalizedMd5 is null || target.NormalizedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase))
                            return offset;
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"MD5 rejected CRC candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.Md5Rejected, offset, actualMd5));
                    }

                    if (offset >= nextProgress)
                    {
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"Scanning {(target.Label ?? target.Crc32Hex)}: offset {offset:N0}"));
                        nextProgress = offset + 256L * 1024 * 1024;
                    }
                    if (offset >= rangeEnd)
                        break;
                }
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ring);
            ArrayPool<byte>.Shared.Return(io);
        }
    }

    private static long? SearchSingleAlignedTargetRange(
        string filePath, long fileLength, HashTarget target, long rangeStart, long rangeEnd, int alignment,
        ref long crcCandidates, int completed, int totalTargets, IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (rangeStart > rangeEnd) return null;
        if (target.Size > int.MaxValue)
            throw new NotSupportedException("Aligned targets larger than 2 GiB are not supported by this search path.");

        int windowLength = checked((int)target.Size);
        int step = Math.Min(alignment, windowLength);
        byte[] ring = ArrayPool<byte>.Shared.Rent(windowLength);
        byte[] io = ArrayPool<byte>.Shared.Rent(Math.Max(IoBufferSize, step));
        var shiftTail = Crc32.CreateShiftOperator(target.Size - step);
        var shiftStep = Crc32.CreateShiftOperator(step);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBufferSize, FileOptions.SequentialScan);
            stream.Position = rangeStart;
            ReadExactly(stream, ring.AsSpan(0, windowLength));
            uint currentCrc = Crc32.Compute(ring.AsSpan(0, windowLength));
            long offset = rangeStart;
            int ringStart = 0;
            long nextProgress = rangeStart;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (currentCrc == target.Crc32)
                {
                    crcCandidates++;
                    progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                        $"CRC32 candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.CrcCandidate, offset));
                    string? actualMd5 = target.NormalizedMd5 is null ? null : ComputeMd5(filePath, offset, target.Size, cancellationToken);
                    if (target.NormalizedMd5 is null || target.NormalizedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase))
                        return offset;
                    progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                        $"MD5 rejected CRC candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.Md5Rejected, offset, actualMd5));
                }

                long stepsRemaining = (rangeEnd - offset) / step;
                if (stepsRemaining <= 0)
                    break;

                int maxBatchSteps = Math.Max(1, io.Length / step);
                int batchSteps = (int)Math.Min(stepsRemaining, maxBatchSteps);
                int batchBytes = checked(batchSteps * step);
                ReadExactly(stream, io.AsSpan(0, batchBytes));

                for (int batch = 0; batch < batchSteps; batch++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    uint outgoingCrc;
                    int first = Math.Min(step, windowLength - ringStart);
                    if (first == step)
                    {
                        outgoingCrc = Crc32.Compute(ring.AsSpan(ringStart, step));
                    }
                    else
                    {
                        byte[] outgoing = ArrayPool<byte>.Shared.Rent(step);
                        try
                        {
                            ring.AsSpan(ringStart, first).CopyTo(outgoing);
                            ring.AsSpan(0, step - first).CopyTo(outgoing.AsSpan(first));
                            outgoingCrc = Crc32.Compute(outgoing.AsSpan(0, step));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(outgoing);
                        }
                    }

                    int incomingOffset = batch * step;
                    uint incomingCrc = Crc32.Compute(io.AsSpan(incomingOffset, step));
                    uint remainder = currentCrc ^ shiftTail.Apply(outgoingCrc);
                    currentCrc = shiftStep.Apply(remainder) ^ incomingCrc;
                    CopyToRing(ring, windowLength, ringStart, io, incomingOffset, step);
                    ringStart = (ringStart + step) % windowLength;
                    offset += step;

                    if (currentCrc == target.Crc32)
                    {
                        crcCandidates++;
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"CRC32 candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.CrcCandidate, offset));
                        string? actualMd5 = target.NormalizedMd5 is null ? null : ComputeMd5(filePath, offset, target.Size, cancellationToken);
                        if (target.NormalizedMd5 is null || target.NormalizedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase))
                            return offset;
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"MD5 rejected CRC candidate at offset {offset:N0} (0x{offset:X})", SearchEventKind.Md5Rejected, offset, actualMd5));
                    }

                    if (offset >= nextProgress)
                    {
                        progress?.Report(new SearchProgress(completed, totalTargets, target, offset, fileLength, crcCandidates,
                            $"Scanning {(target.Label ?? target.Crc32Hex)}: offset {offset:N0}"));
                        nextProgress = offset + 256L * 1024 * 1024;
                    }
                }
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ring);
            ArrayPool<byte>.Shared.Return(io);
        }
    }
/// <summary>
    /// Exhaustive one-byte scanner. This is deliberately separate from the generic scanner:
    /// doing LINQ, GF(2) bit walking, modulo operations, Span slicing and one-byte Stream.Read
    /// calls in a per-byte loop costs an order of magnitude of throughput.
    /// </summary>
    private static bool VerifyExactCandidate(
        string filePath, HashTarget target, long offset, CancellationToken cancellationToken, out string? actualMd5)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBufferSize, FileOptions.SequentialScan);
        stream.Position = offset;
        using var md5 = target.NormalizedMd5 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            long remaining = target.Size;
            uint crc = 0;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read == 0)
                {
                    actualMd5 = null;
                    return false;
                }
                crc = Crc32.Compute(buffer.AsSpan(0, read), crc);
                md5?.AppendData(buffer, 0, read);
                remaining -= read;
            }

            actualMd5 = md5 is null ? null : Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            if (crc != target.Crc32)
                return false;
            return target.NormalizedMd5 is null || target.NormalizedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ExtractMatch(string sourcePath, HashTarget target, long offset, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory();
        string? fileName = target.OutputFileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            string id = target.NormalizedMd5 ?? target.Crc32Hex;
            fileName = $"Match_{offset}_{id}.bin";
        }

        // Never allow pasted filenames to escape the source directory.
        fileName = Path.GetFileName(fileName);
        string outputPath = Path.Combine(directory, fileName);
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to overwrite the source image with an extracted match.");

        string tempPath = outputPath + ".partial";
        try
        {
            // Dispose both streams before renaming the temporary file. Windows will not
            // allow File.Move on a file that is still open with FileShare.None.
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, IoBufferSize, FileOptions.SequentialScan))
            {
                input.Position = offset;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
                try
                {
                    long remaining = target.Size;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int requested = (int)Math.Min(buffer.Length, remaining);
                        int read = input.Read(buffer, 0, requested);
                        if (read == 0) throw new EndOfStreamException();
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    output.Flush(true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            // tempPath is now closed, so this is safe on Windows as well as Linux.
            File.Move(tempPath, outputPath, true);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static string ComputeMd5(string filePath, long offset, long length, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBufferSize, FileOptions.SequentialScan);
        stream.Position = offset;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read == 0)
                    throw new EndOfStreamException();
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static void CopyToRing(byte[] ring, int ringLength, int start, byte[] source, int count) =>
        CopyToRing(ring, ringLength, start, source, 0, count);

    private static void CopyToRing(byte[] ring, int ringLength, int start, byte[] source, int sourceOffset, int count)
    {
        int first = Math.Min(count, ringLength - start);
        source.AsSpan(sourceOffset, first).CopyTo(ring.AsSpan(start, first));
        if (first < count)
            source.AsSpan(sourceOffset + first, count - first).CopyTo(ring.AsSpan(0, count - first));
    }

    private sealed class AlignedState
    {
        public long WindowSize { get; }
        public long WindowSectors { get; }
        public uint CurrentCrc { get; set; }
        public Crc32.ShiftOperator ShiftOutgoing { get; }
        public List<(HashTarget Target, int Index)> Pending { get; }
        public Dictionary<uint, List<(HashTarget Target, int Index)>> PendingByCrc { get; }

        public AlignedState(long windowSize, int sectorSize, List<(HashTarget Target, int Index)> items)
        {
            WindowSize = windowSize;
            WindowSectors = windowSize / sectorSize;
            ShiftOutgoing = Crc32.CreateShiftOperator(windowSize - sectorSize);
            Pending = items;
            PendingByCrc = items.GroupBy(x => x.Target.Crc32).ToDictionary(g => g.Key, g => g.ToList());
        }

        public void Remove((HashTarget Target, int Index) item)
        {
            Pending.Remove(item);
            if (PendingByCrc.TryGetValue(item.Target.Crc32, out var list))
            {
                list.Remove(item);
                if (list.Count == 0)
                    PendingByCrc.Remove(item.Target.Crc32);
            }
        }
    }
}
