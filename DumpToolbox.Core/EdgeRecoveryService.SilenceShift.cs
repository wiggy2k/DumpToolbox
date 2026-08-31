using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class EdgeRecoveryService
{
    private async Task<SearchResult> TryRepairSingleAudioSilenceShiftAsync(
        string source,
        HashTarget target,
        int targetIndex,
        long extentStart,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        long leadingZeros = CountBoundaryZeroBytes(source, extentStart, target.Size, fromStart: true, cancellationToken);
        long trailingZeros = CountBoundaryZeroBytes(source, extentStart, target.Size, fromStart: false, cancellationToken);

        Report(activity, messages,
            $"EDGE: {name}: exact-sized singleton audio extent has {leadingZeros:N0} leading zero byte(s) and {trailingZeros:N0} trailing zero byte(s). Testing only shifts that discard verified zero silence.");

        if (leadingZeros == 0 && trailingZeros == 0)
        {
            string noSilence = $"EDGE: {name}: no zero-byte PCM silence exists at either boundary, so a silence-only shift cannot repair this track.";
            Report(activity, messages, noSilence);
            return new SearchResult(target, extentStart, false, noSilence);
        }

        // If the source has trailing silence, prepend the same amount of virtual
        // silence and let FindCRCs slide over zeros || source. Any accepted shift
        // therefore removes only verified trailing zero bytes.
        if (trailingZeros > 0)
        {
            SearchResult? repaired = await TrySilenceShiftDirectionAsync(
                source, target, targetIndex, extentStart, trailingZeros, padBefore: true,
                outputRoot, activity, messages, cancellationToken).ConfigureAwait(false);
            if (repaired is not null)
                return repaired;
        }

        // Conversely, appending silence permits windows source[k..] || zeros(k),
        // and k is capped by the verified leading zero run.
        if (leadingZeros > 0)
        {
            SearchResult? repaired = await TrySilenceShiftDirectionAsync(
                source, target, targetIndex, extentStart, leadingZeros, padBefore: false,
                outputRoot, activity, messages, cancellationToken).ConfigureAwait(false);
            if (repaired is not null)
                return repaired;
        }

        string verifier = target.NormalizedMd5 is null ? "CRC32" : "CRC32/MD5";
        string failed = $"EDGE: {name}: no {verifier} match was found after testing all signed shifts permitted by its leading/trailing zero-byte silence.";
        Report(activity, messages, failed);
        return new SearchResult(target, extentStart, false, failed);
    }

    private async Task<SearchResult?> TrySilenceShiftDirectionAsync(
        string source,
        HashTarget target,
        int targetIndex,
        long extentStart,
        long paddingLength,
        bool padBefore,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        if (paddingLength <= 0)
            return null;

        string name = TargetDisplayName(target, targetIndex);
        string temp = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.silenceshift");
        try
        {
            string direction = padBefore
                ? "prepend silence / remove trailing silence"
                : "remove leading silence / append silence";
            Report(activity, messages,
                $"EDGE: {name}: FindCRCs silence-shift scan ({direction}), testing 1..{paddingLength:N0} byte(s) at 1-byte alignment.");

            await BuildSilenceShiftSearchSourceAsync(
                source, extentStart, target.Size, paddingLength, padBefore, temp, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SearchResult> scan = await _hashSearch.SearchAsync(
                temp, new[] { target }, alignment: 1, progress: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            SearchResult found = scan[0];
            if (!found.Found || found.Offset is not long paddedOffset)
                return null;

            long shift = padBefore ? paddingLength - paddedOffset : paddedOffset;
            if (shift <= 0 || shift > paddingLength)
                return null;

            string action = padBefore
                ? $"inserted {shift:N0} zero byte(s) at the start and removed {shift:N0} verified trailing zero byte(s)"
                : $"removed {shift:N0} verified leading zero byte(s) and appended {shift:N0} zero byte(s) at the end";
            string frameNote = shift % 4 == 0
                ? $" ({shift / 4:N0} stereo 16-bit PCM frame(s))"
                : " (not 4-byte PCM-frame aligned)";
            string verification = target.NormalizedMd5 is null ? "CRC32 verified" : "CRC32/MD5 verified";
            string fixedMessage = $"EDGE FIXED: {name}: {action}{frameNote}; {verification}.";
            Report(activity, messages, fixedMessage);

            return new SearchResult(
                target, extentStart, true, fixedMessage, found.CrcCandidates, found.OutputPath);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static long CountBoundaryZeroBytes(
        string source,
        long offset,
        long length,
        bool fromStart,
        CancellationToken cancellationToken)
    {
        if (length <= 0)
            return 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.RandomAccess);
            if (offset < 0 || length > input.Length - offset)
                return 0;

            long total = 0;
            if (fromStart)
            {
                input.Position = offset;
                long remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int got = input.Read(buffer, 0, want);
                    if (got <= 0)
                        break;
                    int i = 0;
                    while (i < got && buffer[i] == 0)
                        i++;
                    total += i;
                    if (i != got)
                        break;
                    remaining -= got;
                }
            }
            else
            {
                long remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, remaining);
                    long blockStart = offset + remaining - want;
                    input.Position = blockStart;
                    int got = input.Read(buffer, 0, want);
                    if (got != want)
                        break;
                    int i = got - 1;
                    while (i >= 0 && buffer[i] == 0)
                        i--;
                    total += got - 1 - i;
                    if (i >= 0)
                        break;
                    remaining -= got;
                }
            }
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task BuildSilenceShiftSearchSourceAsync(
        string source,
        long extentStart,
        long extentLength,
        long paddingLength,
        bool padBefore,
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

            if (padBefore)
                await WriteZerosAsync(dest, paddingLength, zeros, cancellationToken).ConfigureAwait(false);

            input.Position = extentStart;
            long remaining = extentLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of source while building the singleton audio silence-shift scan.");
                await dest.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                remaining -= got;
            }

            if (!padBefore)
                await WriteZerosAsync(dest, paddingLength, zeros, cancellationToken).ConfigureAwait(false);

            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static async Task<SearchResult> TryRepairTrailingSilenceOverageAsync(
        string source,
        HashTarget target,
        int targetIndex,
        long expectedStart,
        long removedTrailingSilenceBytes,
        string outputRoot,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string name = TargetDisplayName(target, targetIndex);
        string temp = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.trimtest");
        string finalOutput = GetRecoveredOutputPath(source, target, targetIndex, outputRoot);
        try
        {
            await CopyRangeAsync(source, expectedStart, target.Size, temp, cancellationToken).ConfigureAwait(false);
            if (VerifyFile(temp, target, cancellationToken, out string actualMd5))
            {
                File.Move(temp, finalOutput, true);
                string fixedMessage =
                    $"EDGE FIXED: {name}: removed {removedTrailingSilenceBytes:N0} trailing zero byte(s) from the available final-audio region; CRC32/MD5 verified.";
                Report(activity, messages, fixedMessage);
                return new SearchResult(target, expectedStart, true, fixedMessage, OutputPath: finalOutput);
            }

            string failed =
                $"EDGE: {name}: trimming the {removedTrailingSilenceBytes:N0} verified trailing zero byte(s) still did not match the target (MD5 {actualMd5}); no over-dump repair was accepted.";
            Report(activity, messages, failed);
            return new SearchResult(target, expectedStart, false, failed);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static bool IsRangeAllZero(
        string source,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        if (length <= 0)
            return true;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            if (offset < 0 || length > input.Length - offset)
                return false;
            input.Position = offset;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = input.Read(buffer, 0, want);
                if (got <= 0)
                    return false;
                for (int i = 0; i < got; i++)
                    if (buffer[i] != 0)
                        return false;
                remaining -= got;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}
