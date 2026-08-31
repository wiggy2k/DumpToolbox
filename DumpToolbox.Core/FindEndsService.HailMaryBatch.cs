using System.Buffers;
using System.Collections.Concurrent;

namespace DumpToolbox.Core;

public sealed record HailMaryBatchResult(
    bool Found,
    long? SourceOffset,
    long SearchableLength,
    long InnerZeroBytes,
    long OuterZeroBytes,
    string? OutputPath,
    string? VerifiedMd5,
    long CrcCandidates,
    long WindowsTested,
    string Message);

public sealed partial class FindEndsService
{
    private const int HailMaryBatchBlockSize = 8 * 1024 * 1024;

    private sealed record HailMaryVariant(
        long SearchableLength,
        uint SearchableCrc32,
        long InnerZeroBytes,
        long OuterZeroBytes);

    private sealed class HailMaryLengthPlan
    {
        public required int Length { get; init; }
        public required Dictionary<uint, HailMaryVariant[]> VariantsByCrc { get; init; }
        public required uint[] OutgoingContribution { get; init; }
    }

    private sealed record HailMaryHit(long SourceOffset, HailMaryVariant Variant);

    /// <summary>
    /// Searches every Heads-and-Tails zero-placement variant in a batched CRC pass.
    /// Variants with the same searchable source length share one rolling-window
    /// scan, so inner/outer zero placements are checked together. Source I/O is
    /// performed once in blocks and the distinct window lengths are evaluated in
    /// parallel from the in-memory block.
    /// </summary>
    public Task<HailMaryBatchResult> RunHailMaryBatchAsync(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        long trimmedSilenceBytes,
        string sourceFile,
        string outputFile,
        IProgress<FindEndsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == FindEndsMode.Auto)
            throw new ArgumentException("Heads-and-Tails batch recovery requires MissingStart or MissingEnd.", nameof(mode));
        if (trimmedSilenceBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(trimmedSilenceBytes));
        if (trimmedSilenceBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(trimmedSilenceBytes), "Heads-and-Tails batch recovery currently supports up to Int32.MaxValue edge bytes.");
        if (string.IsNullOrWhiteSpace(partialFile) || !File.Exists(partialFile))
            throw new FileNotFoundException("Partial file not found.", partialFile);
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            throw new FileNotFoundException("Source file not found.", sourceFile);
        if (targetLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetLength));

        string normalizedMd5 = NormalizeMd5(targetMd5);
        string partial = Path.GetFullPath(partialFile);
        string source = Path.GetFullPath(sourceFile);
        string output = Path.GetFullPath(outputFile);
        if (PathsEqual(output, partial) || PathsEqual(output, source))
            throw new InvalidOperationException("Recovered output must be different from the partial and source files.");

        return Task.Run(() => RunHailMaryBatchCore(
            partial, targetLength, targetCrc32, normalizedMd5, mode,
            (int)trimmedSilenceBytes, source, output, progress, cancellationToken), cancellationToken);
    }

    private static HailMaryBatchResult RunHailMaryBatchCore(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        int trimmedSilenceBytes,
        string sourceFile,
        string outputFile,
        IProgress<FindEndsProgress>? progress,
        CancellationToken cancellationToken)
    {
        long partialLength = new FileInfo(partialFile).Length;
        long totalMissing = targetLength - partialLength;
        if (totalMissing <= 0)
            throw new InvalidOperationException("Partial file must be smaller than the full length.");
        if (totalMissing != trimmedSilenceBytes)
            throw new InvalidOperationException($"Trimmed edge length does not match the missing edge ({trimmedSilenceBytes:N0} != {totalMissing:N0}).");

        progress?.Report(new FindEndsProgress(0, 0, 0, "Heads and Tails: calculating CRC targets for all zero-placement variants..."));
        uint partialCrc = ComputeFileCrc32(partialFile, cancellationToken);

        var zeroCrc = new uint[trimmedSilenceBytes + 1];
        Span<byte> zeroByte = stackalloc byte[1];
        zeroByte[0] = 0;
        for (int i = 1; i <= trimmedSilenceBytes; i++)
            zeroCrc[i] = Crc32.Compute(zeroByte, zeroCrc[i - 1]);

        var variants = new List<HailMaryVariant>(1 + trimmedSilenceBytes * 2);
        for (int fixedZeros = 0; fixedZeros <= trimmedSilenceBytes; fixedZeros++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int searchableLength = trimmedSilenceBytes - fixedZeros;

            // No forced zeros is one unique layout. For N > 0, test N zeros at
            // the physical outer edge and N zeros at the inner anchor boundary.
            variants.Add(new HailMaryVariant(
                searchableLength,
                CalculateHailMarySearchableCrc(
                    partialLength, partialCrc, targetCrc32, mode,
                    searchableLength, innerZeroBytes: 0, outerZeroBytes: fixedZeros,
                    zeroCrc[0], zeroCrc[fixedZeros]),
                InnerZeroBytes: 0,
                OuterZeroBytes: fixedZeros));

            if (fixedZeros > 0 && searchableLength > 0)
            {
                variants.Add(new HailMaryVariant(
                    searchableLength,
                    CalculateHailMarySearchableCrc(
                        partialLength, partialCrc, targetCrc32, mode,
                        searchableLength, innerZeroBytes: fixedZeros, outerZeroBytes: 0,
                        zeroCrc[fixedZeros], zeroCrc[0]),
                    InnerZeroBytes: fixedZeros,
                    OuterZeroBytes: 0));
            }
        }

        // The all-zero missing edge is identical regardless of whether those zeros
        // are labelled inner or outer, so verify it once without touching source.
        HailMaryVariant? allZero = variants.FirstOrDefault(v => v.SearchableLength == 0);
        if (allZero is not null)
        {
            string md5 = ComputeReconstructedMd5WithBoundaryZeros(
                partialFile, sourceFile, 0, 0, mode,
                allZero.OuterZeroBytes, allZero.InnerZeroBytes, cancellationToken);
            if (md5.Equals(targetMd5, StringComparison.OrdinalIgnoreCase))
            {
                string written = WriteReconstructedFileWithBoundaryZeros(
                    partialFile, sourceFile, 0, 0, mode,
                    allZero.OuterZeroBytes, allZero.InnerZeroBytes,
                    outputFile, targetMd5, cancellationToken);
                return new HailMaryBatchResult(true, 0, 0,
                    allZero.InnerZeroBytes, allZero.OuterZeroBytes,
                    outputFile, written, 1, 0,
                    "Recovered the Heads-and-Tails edge using only forced zero bytes.");
            }
        }

        HailMaryLengthPlan[] plans = BuildHailMaryLengthPlans(variants.Where(v => v.SearchableLength > 0));
        if (plans.Length == 0)
            return new HailMaryBatchResult(false, null, 0, 0, 0, null, null, 1, 0,
                "No searchable Heads-and-Tails source variants remain.");

        long sourceLength = new FileInfo(sourceFile).Length;
        int maxLength = plans.Max(p => p.Length);
        long totalWindows = 0;
        foreach (HailMaryLengthPlan plan in plans)
        {
            if (sourceLength >= plan.Length)
                totalWindows = checked(totalWindows + (sourceLength - plan.Length + 1));
        }

        int workers = Math.Max(1, Environment.ProcessorCount - 1);
        progress?.Report(new FindEndsProgress(0, Math.Max(1, sourceLength), 0,
            $"Heads and Tails: {variants.Count:N0} CRC target layout(s), {plans.Length:N0} distinct source length(s), one blockwise source read; using up to {workers:N0} CRC worker(s)."));

        long crcCandidates = 0;
        long windowsTested = 0;
        uint[] oneByteCrc = BuildOneByteCrcTable();
        uint[][] shiftOneTables = Crc32.CreateShiftOperator(1).CreateByteTables();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HailMaryBatchBlockSize + maxLength);
        try
        {
            using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, HailMaryBatchBlockSize, FileOptions.SequentialScan);

            long blockStart = 0;
            while (blockStart < sourceLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int mainLength = (int)Math.Min(HailMaryBatchBlockSize, sourceLength - blockStart);
                int readLength = (int)Math.Min((long)mainLength + maxLength - 1L, sourceLength - blockStart);
                source.Position = blockStart;
                int got = ReadSome(source, buffer, readLength);
                if (got < mainLength)
                    throw new EndOfStreamException("Unexpected end of Heads-and-Tails source while reading a search block.");

                var hits = new ConcurrentBag<HailMaryHit>();
                long blockWindows = 0;
                object windowsLock = new();

                Parallel.ForEach(
                    plans,
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers },
                    plan =>
                    {
                        long maxStartAbsolute = sourceLength - plan.Length;
                        if (blockStart > maxStartAbsolute)
                            return;

                        int startCount = (int)Math.Min(mainLength, maxStartAbsolute - blockStart + 1);
                        if (startCount <= 0)
                            return;

                        uint currentCrc = Crc32.Compute(buffer.AsSpan(0, plan.Length));
                        CheckHailMaryWindow(plan, currentCrc, blockStart, hits);

                        for (int start = 1; start < startCount; start++)
                        {
                            if ((start & 0x3FFFF) == 0)
                                cancellationToken.ThrowIfCancellationRequested();

                            byte outgoing = buffer[start - 1];
                            byte incoming = buffer[start + plan.Length - 1];
                            uint remainder = currentCrc ^ plan.OutgoingContribution[outgoing];
                            currentCrc = Crc32.ShiftOperator.ApplyByteTables(shiftOneTables, remainder)
                                       ^ oneByteCrc[incoming];
                            CheckHailMaryWindow(plan, currentCrc, blockStart + start, hits);
                        }

                        lock (windowsLock)
                            blockWindows += startCount;
                    });

                windowsTested = checked(windowsTested + blockWindows);

                if (!hits.IsEmpty)
                {
                    foreach (HailMaryHit hit in hits.OrderBy(h => h.SourceOffset))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        crcCandidates++;
                        string actualMd5 = ComputeReconstructedMd5WithBoundaryZeros(
                            partialFile, sourceFile, hit.SourceOffset, hit.Variant.SearchableLength,
                            mode, hit.Variant.OuterZeroBytes, hit.Variant.InnerZeroBytes, cancellationToken);

                        progress?.Report(new FindEndsProgress(
                            Math.Min(sourceLength, blockStart + mainLength), Math.Max(1, sourceLength), crcCandidates,
                            $"Heads and Tails CRC candidate at source offset {hit.SourceOffset:N0}: {hit.Variant.SearchableLength:N0} source + {hit.Variant.InnerZeroBytes:N0} inner/{hit.Variant.OuterZeroBytes:N0} outer zero byte(s); verifying MD5..."));

                        if (!actualMd5.Equals(targetMd5, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string writtenMd5 = WriteReconstructedFileWithBoundaryZeros(
                            partialFile, sourceFile, hit.SourceOffset, hit.Variant.SearchableLength,
                            mode, hit.Variant.OuterZeroBytes, hit.Variant.InnerZeroBytes,
                            outputFile, targetMd5, cancellationToken);

                        return new HailMaryBatchResult(
                            true, hit.SourceOffset, hit.Variant.SearchableLength,
                            hit.Variant.InnerZeroBytes, hit.Variant.OuterZeroBytes,
                            outputFile, writtenMd5, crcCandidates, windowsTested,
                            $"Recovered Heads-and-Tails edge from source offset {hit.SourceOffset:N0}." );
                    }
                }

                blockStart += mainLength;
                progress?.Report(new FindEndsProgress(
                    blockStart, Math.Max(1, sourceLength), crcCandidates,
                    $"Heads and Tails batched CRC scan: source {blockStart:N0}/{sourceLength:N0} bytes; {windowsTested:N0}/{totalWindows:N0} variable-length windows tested; {crcCandidates:N0} CRC candidate(s)."));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new HailMaryBatchResult(
            false, null, 0, 0, 0, null, null,
            crcCandidates, windowsTested,
            crcCandidates == 0
                ? "No source window matched any calculated Heads-and-Tails CRC target."
                : $"{crcCandidates:N0} CRC candidate(s) were found, but none reconstructed to the expected MD5.");
    }

    private static HailMaryLengthPlan[] BuildHailMaryLengthPlans(IEnumerable<HailMaryVariant> variants)
    {
        uint[] oneByteCrc = BuildOneByteCrcTable();
        return variants
            .GroupBy(v => checked((int)v.SearchableLength))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                int length = g.Key;
                var shiftTail = Crc32.CreateShiftOperator(length - 1);
                var outgoingContribution = new uint[256];
                for (int value = 0; value < 256; value++)
                    outgoingContribution[value] = shiftTail.Apply(oneByteCrc[value]);

                return new HailMaryLengthPlan
                {
                    Length = length,
                    VariantsByCrc = g.GroupBy(v => v.SearchableCrc32)
                        .ToDictionary(x => x.Key, x => x.ToArray()),
                    OutgoingContribution = outgoingContribution
                };
            })
            .ToArray();
    }

    private static uint[] BuildOneByteCrcTable()
    {
        var table = new uint[256];
        Span<byte> one = stackalloc byte[1];
        for (int value = 0; value < 256; value++)
        {
            one[0] = (byte)value;
            table[value] = Crc32.Compute(one);
        }
        return table;
    }

    private static void CheckHailMaryWindow(
        HailMaryLengthPlan plan,
        uint crc,
        long sourceOffset,
        ConcurrentBag<HailMaryHit> hits)
    {
        if (!plan.VariantsByCrc.TryGetValue(crc, out HailMaryVariant[]? matching) || matching is null)
            return;

        foreach (HailMaryVariant variant in matching)
            hits.Add(new HailMaryHit(sourceOffset, variant));
    }

    private static uint CalculateHailMarySearchableCrc(
        long partialLength,
        uint partialCrc,
        uint targetCrc32,
        FindEndsMode mode,
        long searchableLength,
        long innerZeroBytes,
        long outerZeroBytes,
        uint innerZeroCrc,
        uint outerZeroCrc)
    {
        if (mode == FindEndsMode.MissingEnd)
        {
            // target = partial || inner-zeros || searchable || outer-zeros
            uint beforeOuter = outerZeroBytes == 0
                ? targetCrc32
                : Crc32.CreateInverseShiftOperator(outerZeroBytes).Apply(targetCrc32 ^ outerZeroCrc);

            uint partialAndInner = innerZeroBytes == 0
                ? partialCrc
                : Crc32.CreateShiftOperator(innerZeroBytes).Apply(partialCrc) ^ innerZeroCrc;

            return beforeOuter ^ Crc32.CreateShiftOperator(searchableLength).Apply(partialAndInner);
        }

        // target = outer-zeros || searchable || inner-zeros || partial
        uint beforePartial = Crc32.CreateInverseShiftOperator(partialLength).Apply(targetCrc32 ^ partialCrc);
        uint beforeInner = innerZeroBytes == 0
            ? beforePartial
            : Crc32.CreateInverseShiftOperator(innerZeroBytes).Apply(beforePartial ^ innerZeroCrc);

        return beforeInner ^ Crc32.CreateShiftOperator(searchableLength).Apply(outerZeroCrc);
    }
}
