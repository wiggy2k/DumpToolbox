using System.Buffers;

namespace DumpToolbox.Core;

public sealed partial class EdgeRecoveryService
{
    private const int CddaPcmSampleBytes = 2;

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }


    private async Task<SearchResult> TryHailMaryUnderdumpedAudioEdgeAsync(
        string source,
        string searchSource,
        HashTarget target,
        int targetIndex,
        long partialOffset,
        long partialLength,
        FindEndsMode mode,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        string side = mode == FindEndsMode.MissingStart ? "start" : "end";
        string? expectedMd5 = target.NormalizedMd5;
        long missingLength = target.Size - partialLength;

        if (expectedMd5 is null)
        {
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: proven under-dumped {side} edge has no MD5, so exhaustive recovery is skipped.");
            return new SearchResult(target, partialOffset, false, $"HEADS AND TAILS: {name}: MD5 is required.");
        }

        if (partialLength <= 0 || missingLength <= 0 || missingLength > int.MaxValue)
        {
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: proven under-dumped {side} edge has an unsupported missing length of {missingLength:N0} byte(s); fallback not applicable.");
            return new SearchResult(target, partialOffset, false, $"HEADS AND TAILS: {name}: unsupported missing length.");
        }

        string partialTemp = Path.Combine(outputRoot, $".dumptoolbox_heads_tails_{Guid.NewGuid():N}.partial");
        string finalOutput = GetRecoveredOutputPath(source, target, targetIndex, outputRoot);
        long expectedStart = mode == FindEndsMode.MissingStart
            ? checked(partialOffset - missingLength)
            : partialOffset;

        try
        {
            await CopyRangeAsync(source, partialOffset, partialLength, partialTemp, cancellationToken).ConfigureAwait(false);

            long sourceLength = new FileInfo(searchSource).Length;
            long layoutCount = checked(missingLength * 2);
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: normal zero-fill and missing-segment Find Ends recovery failed for a proven {missingLength:N0}-byte under-dump at the {side}. " +
                $"Using the {partialLength:N0} known byte(s) as the anchor and CRC algebra to test all allowed source/zero placements for the missing edge.");
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: Heads and Tails corpus is {sourceLength:N0} byte(s); {layoutCount:N0} inner/outer zero-placement layout(s) will be represented by {missingLength:N0} distinct source-window length(s) in the batched CRC scan.");

            int nextPercent = 5;
            var batchProgress = new InlineProgress<FindEndsProgress>(p =>
            {
                if (p.SearchableOffsets > 0)
                {
                    int percent = (int)Math.Floor(p.Fraction * 100.0);
                    if (percent < nextPercent && p.Offset < p.SearchableOffsets && !p.Message.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                        return;
                    while (nextPercent <= percent)
                        nextPercent += 5;
                }
                Report(activity, messages, p.Message);
            });

            HailMaryBatchResult attempt = await _findEnds.RunHailMaryBatchAsync(
                partialTemp,
                target.Size,
                target.Crc32,
                expectedMd5,
                mode,
                checked((int)missingLength),
                searchSource,
                finalOutput,
                batchProgress,
                cancellationToken).ConfigureAwait(false);

            if (attempt.Found && !string.IsNullOrWhiteSpace(attempt.OutputPath))
            {
                string fixedMessage =
                    $"HEADS AND TAILS FIXED: {name}: recovered the proven {missingLength:N0}-byte {side} under-dump using {attempt.SearchableLength:N0} byte(s) from Heads and Tails corpus offset {attempt.SourceOffset:N0}, " +
                    $"with {attempt.InnerZeroBytes:N0} inner and {attempt.OuterZeroBytes:N0} outer forced 00 byte(s); CRC32/MD5 verified.";
                Report(activity, messages, fixedMessage);
                return new SearchResult(target, expectedStart, true, fixedMessage, attempt.CrcCandidates, attempt.OutputPath);
            }

            string failed =
                $"HEADS AND TAILS: {name}: batched CRC search exhausted all allowed source/zero layouts for the proven {missingLength:N0}-byte {side} under-dump; no CRC32/MD5-verified reconstruction exists in the Heads and Tails corpus. " +
                $"Tested {attempt.WindowsTested:N0} variable-length source windows and found {attempt.CrcCandidates:N0} CRC candidate(s).";
            Report(activity, messages, failed);
            return new SearchResult(target, expectedStart, false, failed, attempt.CrcCandidates);
        }
        finally
        {
            TryDelete(partialTemp);
        }
    }

    private async Task<SearchResult> TryHailMaryExactSizedAudioEdgeAsync(
        string source,
        string searchSource,
        HashTarget target,
        int targetIndex,
        long extentStart,
        FindEndsMode mode,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        string side = mode == FindEndsMode.MissingStart ? "start" : "end";
        string? expectedMd5 = target.NormalizedMd5;
        if (expectedMd5 is null)
        {
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: exact-length {side}-edge candidate has no MD5, so exhaustive recovery is skipped.");
            return new SearchResult(target, extentStart, false, $"HEADS AND TAILS: {name}: MD5 is required.");
        }

        long silenceBytes = CountOuterZeroPcmSilenceBytes(
            source, extentStart, target.Size, mode, cancellationToken);
        if (silenceBytes <= 0)
        {
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: exact-length unmatched outer track has no whole zero-valued 16-bit PCM sample at the physical {side}; fallback not applicable.");
            return new SearchResult(target, extentStart, false, $"HEADS AND TAILS: {name}: no zero PCM silence at the {side}.");
        }

        long partialOffset = mode == FindEndsMode.MissingStart
            ? checked(extentStart + silenceBytes)
            : extentStart;
        long partialLength = checked(target.Size - silenceBytes);
        if (partialLength <= 0)
        {
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: the entire exact-sized track is zero PCM silence; no non-zero audio anchor remains after trimming.");
            return new SearchResult(target, extentStart, false, $"HEADS AND TAILS: {name}: entire track is silence.");
        }

        string partialTemp = Path.Combine(outputRoot, $".dumptoolbox_heads_tails_{Guid.NewGuid():N}.partial");
        string finalOutput = GetRecoveredOutputPath(source, target, targetIndex, outputRoot);
        long totalCandidates = 0;
        try
        {
            await CopyRangeAsync(source, partialOffset, partialLength, partialTemp, cancellationToken).ConfigureAwait(false);

            long sourceLength = new FileInfo(searchSource).Length;
            long layoutCount = checked(silenceBytes * 2);
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: exact expected length and adjacent anchor are valid, but the track does not hash-match. " +
                $"Removed {silenceBytes:N0} byte(s) ({silenceBytes / CddaPcmSampleBytes:N0} 16-bit PCM sample(s)) of verified digital silence from the physical {side}, leaving {partialLength:N0} anchored byte(s). " +
                $"Using CRC algebra to derive all {layoutCount:N0} allowed missing-source/zero-placement targets, then scanning the Heads and Tails corpus blockwise instead of re-reading it once per split.");
            Report(activity, messages,
                $"HEADS AND TAILS: {name}: Heads and Tails corpus is {sourceLength:N0} byte(s). Inner/outer zero placements sharing the same source length are checked in one rolling-CRC scan, and distinct lengths are processed in parallel from each in-memory source block.");

            int nextPercent = 5;
            var batchProgress = new InlineProgress<FindEndsProgress>(p =>
            {
                if (p.SearchableOffsets > 0)
                {
                    int percent = (int)Math.Floor(p.Fraction * 100.0);
                    if (percent < nextPercent && p.Offset < p.SearchableOffsets && !p.Message.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                        return;

                    while (nextPercent <= percent)
                        nextPercent += 5;
                }

                Report(activity, messages, p.Message);
            });

            HailMaryBatchResult attempt = await _findEnds.RunHailMaryBatchAsync(
                partialTemp,
                target.Size,
                target.Crc32,
                expectedMd5,
                mode,
                silenceBytes,
                searchSource,
                finalOutput,
                batchProgress,
                cancellationToken).ConfigureAwait(false);

            totalCandidates = attempt.CrcCandidates;
            if (attempt.Found && !string.IsNullOrWhiteSpace(attempt.OutputPath))
            {
                string fixedMessage =
                    $"HEADS AND TAILS FIXED: {name}: recovered the {side} edge using {attempt.SearchableLength:N0} byte(s) from Heads and Tails corpus offset {attempt.SourceOffset:N0}, " +
                    $"with {attempt.InnerZeroBytes:N0} inner and {attempt.OuterZeroBytes:N0} outer forced 00 byte(s); CRC32/MD5 verified.";
                Report(activity, messages, fixedMessage);
                return new SearchResult(target, extentStart, true, fixedMessage, totalCandidates, attempt.OutputPath);
            }

            string failed =
                $"HEADS AND TAILS: {name}: batched CRC search exhausted all {layoutCount:N0} allowed inner/outer zero-placement layouts across {silenceBytes:N0} trimmed edge byte(s); no CRC32/MD5-verified reconstruction exists in the Heads and Tails corpus. " +
                $"Tested {attempt.WindowsTested:N0} variable-length source windows and found {attempt.CrcCandidates:N0} CRC candidate(s).";
            Report(activity, messages, failed);
            return new SearchResult(target, extentStart, false, failed, totalCandidates);
        }
        finally
        {
            TryDelete(partialTemp);
        }
    }

    private static long CountOuterZeroPcmSilenceBytes(
        string source,
        long extentStart,
        long extentLength,
        FindEndsMode mode,
        CancellationToken cancellationToken)
    {
        if (extentLength < CddaPcmSampleBytes)
            return 0;

        long sampleAlignedLength = extentLength - (extentLength % CddaPcmSampleBytes);
        return mode == FindEndsMode.MissingStart
            ? CountLeadingZeroPcmSamples(source, extentStart, sampleAlignedLength, cancellationToken) * CddaPcmSampleBytes
            : CountTrailingZeroPcmSamples(source, extentStart, sampleAlignedLength, cancellationToken) * CddaPcmSampleBytes;
    }

    private static long CountLeadingZeroPcmSamples(
        string source,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            stream.Position = offset;
            long remaining = length;
            long zeroSamples = 0;
            while (remaining >= CddaPcmSampleBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length - (buffer.Length % CddaPcmSampleBytes), remaining);
                int got = ReadSome(stream, buffer, want);
                got -= got % CddaPcmSampleBytes;
                if (got <= 0)
                    break;

                for (int i = 0; i < got; i += CddaPcmSampleBytes)
                {
                    if ((buffer[i] | buffer[i + 1]) != 0)
                        return zeroSamples;
                    zeroSamples++;
                }
                remaining -= got;
            }
            return zeroSamples;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long CountTrailingZeroPcmSamples(
        string source,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.RandomAccess);
            long remaining = length;
            long zeroSamples = 0;
            while (remaining >= CddaPcmSampleBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length - (buffer.Length % CddaPcmSampleBytes), remaining);
                long chunkStart = offset + remaining - want;
                stream.Position = chunkStart;
                int got = ReadSome(stream, buffer, want);
                got -= got % CddaPcmSampleBytes;
                if (got <= 0)
                    break;

                for (int i = got - CddaPcmSampleBytes; i >= 0; i -= CddaPcmSampleBytes)
                {
                    if ((buffer[i] | buffer[i + 1]) != 0)
                        return zeroSamples;
                    zeroSamples++;
                }
                remaining -= got;
            }
            return zeroSamples;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadSome(FileStream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int got = stream.Read(buffer, total, count - total);
            if (got <= 0)
                break;
            total += got;
        }
        return total;
    }

}
