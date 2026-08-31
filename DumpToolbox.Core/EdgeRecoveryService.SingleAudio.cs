using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class EdgeRecoveryService
{
    private static bool TryGetSingleAudioExtent(
        long sourceLength,
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results,
        int audioIndex,
        out long extentStart,
        out long extentLength,
        out string description)
    {
        extentStart = 0;
        extentLength = 0;
        description = string.Empty;

        long? start = null;
        string? startDescription = null;
        if (audioIndex == 0)
        {
            start = 0;
            startDescription = "source offset zero";
        }
        else if (results[audioIndex - 1].Found && results[audioIndex - 1].Offset is long previousOffset)
        {
            start = checked(previousOffset + targets[audioIndex - 1].Size);
            startDescription = $"end of matched {TargetDisplayName(targets[audioIndex - 1], audioIndex - 1)}";
        }
        else
        {
            for (int anchor = audioIndex - 2; anchor >= 0 && start is null; anchor--)
            {
                if (!results[anchor].Found || results[anchor].Offset is not long anchorOffset)
                    continue;

                long projected = anchorOffset;
                for (int i = anchor; i < audioIndex; i++)
                    projected = checked(projected + targets[i].Size);
                start = projected;
                startDescription = $"boundary projected forward from matched {TargetDisplayName(targets[anchor], anchor)}";
            }
        }

        long? end = null;
        string? endDescription = null;
        if (audioIndex == targets.Count - 1)
        {
            end = sourceLength;
            endDescription = "source EOF";
        }
        else if (results[audioIndex + 1].Found && results[audioIndex + 1].Offset is long nextOffset)
        {
            end = nextOffset;
            endDescription = $"start of matched {TargetDisplayName(targets[audioIndex + 1], audioIndex + 1)}";
        }
        else
        {
            for (int anchor = audioIndex + 2; anchor < targets.Count && end is null; anchor++)
            {
                if (!results[anchor].Found || results[anchor].Offset is not long anchorOffset)
                    continue;

                long projected = anchorOffset;
                for (int i = anchor - 1; i >= audioIndex + 1; i--)
                    projected = checked(projected - targets[i].Size);
                end = projected;
                endDescription = $"boundary projected backward from matched {TargetDisplayName(targets[anchor], anchor)}";
            }
        }

        if (start is null || end is null || start.Value < 0 || end.Value < start.Value || end.Value > sourceLength)
            return false;

        extentStart = start.Value;
        extentLength = end.Value - start.Value;
        description = $"{startDescription} to {endDescription}";
        return true;
    }

    private async Task<SearchResult> TryRepairShortSingleAudioZeroPaddingAsync(
        string source,
        HashTarget target,
        int targetIndex,
        long extentStart,
        long extentLength,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        long shortfall = target.Size - extentLength;
        if (shortfall <= 0 || extentLength <= 0)
            return new SearchResult(target, extentStart, false, $"EDGE: {name}: source extent is not short, so zero-padding recovery was skipped.");

        string temp = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.shortpadscan");
        try
        {
            Report(activity, messages,
                $"EDGE: {name}: building a zero-silence padding scan for the {shortfall:N0}-byte shortfall. Every split is tested at 1-byte precision: all silence at the start, all at the end, and every distribution between them.");

            await BuildShortAudioPaddingSearchSourceAsync(
                source, extentStart, extentLength, shortfall, temp, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SearchResult> scan = await _hashSearch.SearchAsync(
                temp, new[] { target }, alignment: 1, progress: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            SearchResult found = scan[0];
            if (!found.Found || found.Offset is not long paddedOffset || paddedOffset < 0 || paddedOffset > shortfall)
            {
                string verifier = target.NormalizedMd5 is null ? "CRC32" : "CRC32/MD5";
                Report(activity, messages,
                    $"EDGE: {name}: no {verifier} match was found after testing all {shortfall + 1:N0} direct zero-padding split(s) for the {shortfall:N0}-byte shortfall. Testing the padded track again with signed silence shifts.");

                SearchResult shiftedPadded = await TryRepairShortSingleAudioPaddingAndShiftAsync(
                    source, target, targetIndex, extentStart, extentLength, shortfall, outputRoot,
                    activity, messages, cancellationToken).ConfigureAwait(false);
                if (shiftedPadded.Found)
                    return shiftedPadded;

                string failed =
                    $"EDGE: {name}: no {verifier} match was found after exhaustive short-track zero padding plus safe signed silence shifting.";
                Report(activity, messages, failed);
                return new SearchResult(
                    target, extentStart, false, failed,
                    found.CrcCandidates + shiftedPadded.CrcCandidates);
            }

            long zerosAtStart = shortfall - paddedOffset;
            long zerosAtEnd = paddedOffset;
            string verification = target.NormalizedMd5 is null ? "CRC32 verified" : "CRC32/MD5 verified";
            string frameNote = shortfall % 4 == 0
                ? $" ({shortfall / 4:N0} total stereo 16-bit PCM frame(s) of silence)"
                : " (total padding is not 4-byte PCM-frame aligned)";
            string fixedMessage =
                $"EDGE FIXED: {name}: restored {shortfall:N0} missing zero byte(s): {zerosAtStart:N0} at the start and {zerosAtEnd:N0} at the end{frameNote}; {verification}.";
            Report(activity, messages, fixedMessage);

            return new SearchResult(
                target, extentStart, true, fixedMessage, found.CrcCandidates, found.OutputPath);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task BuildShortAudioPaddingSearchSourceAsync(
        string source,
        long extentStart,
        long extentLength,
        long shortfall,
        string output,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        Array.Clear(zeros, 0, zeros.Length);
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dest = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await WriteZerosAsync(dest, shortfall, zeros, cancellationToken).ConfigureAwait(false);

            input.Position = extentStart;
            long remaining = extentLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of source while building the short singleton-audio zero-padding scan.");
                await dest.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                remaining -= got;
            }

            await WriteZerosAsync(dest, shortfall, zeros, cancellationToken).ConfigureAwait(false);
            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private async Task<SearchResult> TryRepairShortSingleAudioPaddingAndShiftAsync(
        string source,
        HashTarget target,
        int targetIndex,
        long extentStart,
        long extentLength,
        long shortfall,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        long leadingZeros = CountBoundaryZeroBytes(
            source, extentStart, extentLength, fromStart: true, cancellationToken);
        long trailingZeros = CountBoundaryZeroBytes(
            source, extentStart, extentLength, fromStart: false, cancellationToken);

        Report(activity, messages,
            $"EDGE: {name}: short source extent has {leadingZeros:N0} verified leading zero byte(s) and {trailingZeros:N0} verified trailing zero byte(s). " +
            $"The combined scan will add the required {shortfall:N0} missing zero byte(s) and also test every signed shift that discards only those verified boundary zeros.");

        if (leadingZeros == 0 && trailingZeros == 0)
        {
            string noShift =
                $"EDGE: {name}: the direct padding splits failed and the short source extent has no existing boundary zero silence to shift.";
            Report(activity, messages, noShift);
            return new SearchResult(target, extentStart, false, noShift);
        }

        // Let:
        //   M = source extent length
        //   N = shortfall, so target length = M + N
        //   L/T = verified leading/trailing zero runs in the source extent.
        //
        // Build:
        //   zeros(N + T) || source || zeros(N + L)
        //
        // Every target-sized window in that stream is safe. Moving the window
        // through the first T positions can only discard verified trailing
        // source zeros; moving it through the last L positions can only discard
        // verified leading source zeros. The central N+1 positions are the
        // direct start/end padding splits already tried above. Searching the
        // whole stream therefore exhaustively covers "pad the shortfall, then
        // shift within existing zero silence" in both directions without ever
        // discarding non-zero PCM.
        long prefixZeros = checked(shortfall + trailingZeros);
        long suffixZeros = checked(shortfall + leadingZeros);
        long candidateCount = checked(shortfall + leadingZeros + trailingZeros + 1);

        string temp = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.shortpadshift");
        try
        {
            Report(activity, messages,
                $"EDGE: {name}: combined padded+shifted FindCRCs scan has {candidateCount:N0} target-sized window(s) at 1-byte alignment.");

            await BuildShortAudioPaddingAndShiftSearchSourceAsync(
                source, extentStart, extentLength, prefixZeros, suffixZeros,
                temp, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SearchResult> scan = await _hashSearch.SearchAsync(
                temp, new[] { target }, alignment: 1, progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            SearchResult found = scan[0];
            if (!found.Found || found.Offset is not long windowOffset)
            {
                string verifier = target.NormalizedMd5 is null ? "CRC32" : "CRC32/MD5";
                string failed =
                    $"EDGE: {name}: no {verifier} match was found in the combined padded+shifted scan.";
                Report(activity, messages, failed);
                return new SearchResult(target, extentStart, false, failed, found.CrcCandidates);
            }

            long sourceInSearchStart = prefixZeros;
            long sourceInSearchEnd = checked(prefixZeros + extentLength);
            long windowEnd = checked(windowOffset + target.Size);

            long trimmedLeading = Math.Max(0, windowOffset - sourceInSearchStart);
            long trimmedTrailing = Math.Max(0, sourceInSearchEnd - windowEnd);
            long zerosAtStart = Math.Max(0, sourceInSearchStart - windowOffset);
            long zerosAtEnd = Math.Max(0, windowEnd - sourceInSearchEnd);

            // Guaranteed by the construction above, but keep the guard so a
            // future refactor cannot silently broaden recovery into non-zero PCM.
            if (trimmedLeading > leadingZeros || trimmedTrailing > trailingZeros)
            {
                string unsafeMessage =
                    $"EDGE: {name}: a combined padding/shift hash hit would discard non-zero source audio; rejecting it.";
                Report(activity, messages, unsafeMessage);
                return new SearchResult(target, extentStart, false, unsafeMessage, found.CrcCandidates);
            }

            string verification = target.NormalizedMd5 is null
                ? "CRC32 verified"
                : "CRC32/MD5 verified";
            string frameNote =
                (zerosAtStart + zerosAtEnd) % 4 == 0 &&
                trimmedLeading % 4 == 0 &&
                trimmedTrailing % 4 == 0
                    ? $" ({(zerosAtStart + zerosAtEnd) / 4:N0} inserted stereo 16-bit PCM silence frame(s))"
                    : " (byte-precise shift; not wholly 4-byte PCM-frame aligned)";

            string fixedMessage =
                $"EDGE FIXED: {name}: short-track padding plus silence shift matched. " +
                $"Inserted {zerosAtStart:N0} zero byte(s) at the start and {zerosAtEnd:N0} at the end; " +
                $"discarded {trimmedLeading:N0} verified leading and {trimmedTrailing:N0} verified trailing zero byte(s) from the available source{frameNote}; {verification}.";
            Report(activity, messages, fixedMessage);

            return new SearchResult(
                target, extentStart, true, fixedMessage, found.CrcCandidates, found.OutputPath);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task BuildShortAudioPaddingAndShiftSearchSourceAsync(
        string source,
        long extentStart,
        long extentLength,
        long prefixZeros,
        long suffixZeros,
        string output,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        Array.Clear(zeros, 0, zeros.Length);
        try
        {
            await using var input = new FileStream(
                source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dest = new FileStream(
                output, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await WriteZerosAsync(dest, prefixZeros, zeros, cancellationToken).ConfigureAwait(false);

            input.Position = extentStart;
            long remaining = extentLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = await input.ReadAsync(
                    buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got <= 0)
                    throw new EndOfStreamException(
                        "Unexpected end of source while building the short singleton-audio padded+shift scan.");

                await dest.WriteAsync(
                    buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                remaining -= got;
            }

            await WriteZerosAsync(dest, suffixZeros, zeros, cancellationToken).ConfigureAwait(false);
            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

}
