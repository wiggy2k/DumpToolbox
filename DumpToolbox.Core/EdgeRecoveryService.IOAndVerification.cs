using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class EdgeRecoveryService
{
    private const int CddaPcmFrameBytes = 4;

    private enum AudioPartialTrim
    {
        None,
        LeadingZeroFrames,
        TrailingZeroFrames
    }

    private async Task<SearchResult> RepairOneAsync(
        string source,
        long sourceLength,
        HashTarget target,
        int targetIndex,
        long partialOffset,
        long partialLength,
        FindEndsMode missingMode,
        long expectedStart,
        string outputRoot,
        bool savePartialOnFailure,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken,
        AudioPartialTrim partialTrim = AudioPartialTrim.None)
    {
        string side = missingMode == FindEndsMode.MissingStart ? "start" : "end";
        long missingLength = target.Size - partialLength;
        string name = TargetDisplayName(target, targetIndex);

        if (missingLength <= 0 || partialLength < 0 || partialOffset < 0 || partialOffset + partialLength > sourceLength)
            return new SearchResult(target, null, false, "Not found; edge geometry was not suitable for under-dump recovery.");

        string? expectedMd5 = target.NormalizedMd5;
        if (expectedMd5 is null)
        {
            string noMd5 = $"EDGE: {name}: {missingLength:N0} bytes appear missing at the {side}, but MD5 is required for safe reconstruction.";
            activity?.Report(noMd5);
            messages.Add(noMd5);
            if (savePartialOnFailure)
            {
                long inspectionOffset = partialTrim == AudioPartialTrim.None ? expectedStart : partialOffset;
                long inspectionLength = partialTrim == AudioPartialTrim.None ? target.Size : partialLength;
                SearchResult inspected = await SaveInspectionPartialAsync(
                    source, inspectionOffset, inspectionLength, target, targetIndex, outputRoot,
                    new SearchResult(target, expectedStart, false, noMd5),
                    "target-sized window derived from the adjacent audio anchor",
                    activity, messages, cancellationToken, partialTrim).ConfigureAwait(false);
                return inspected;
            }
            return new SearchResult(target, expectedStart, false, noMd5);
        }

        string partialTemp = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.partialdata");
        string zeroCandidate = Path.Combine(outputRoot, $".dumptoolbox_edge_{Guid.NewGuid():N}.zerotest");
        string finalOutput = GetRecoveredOutputPath(source, target, targetIndex, outputRoot);

        try
        {
            await CopyRangeAsync(source, partialOffset, partialLength, partialTemp, cancellationToken).ConfigureAwait(false);

            string detected = $"EDGE: {name}: adjacent match proves {missingLength:N0} byte(s) missing at the {side}; trying digital silence first.";
            activity?.Report(detected);
            messages.Add(detected);

            await BuildZeroCandidateAsync(partialTemp, zeroCandidate, missingLength, missingMode, cancellationToken).ConfigureAwait(false);
            if (VerifyFile(zeroCandidate, target, cancellationToken, out string zeroMd5))
            {
                File.Move(zeroCandidate, finalOutput, true);
                string fixedZero = $"EDGE FIXED: {name}: {missingLength:N0} zero byte(s) restored at the {side}; CRC32/MD5 verified.";
                activity?.Report(fixedZero);
                messages.Add(fixedZero);
                TryDelete(partialTemp);
                return new SearchResult(target, expectedStart, true, fixedZero, OutputPath: finalOutput);
            }

            TryDelete(zeroCandidate);
            string zeroFailed = $"EDGE: {name}: zero padding did not match (MD5 {zeroMd5}); calculating missing-segment CRC32 and searching the complete source.";
            activity?.Report(zeroFailed);
            messages.Add(zeroFailed);

            FindEndsResult findEnds = await _findEnds.RunAsync(
                partialTemp,
                target.Size,
                target.Crc32,
                expectedMd5,
                missingMode,
                source,
                finalOutput,
                progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (FindEndsAnalysis analysis in findEnds.Analyses)
            {
                string analysisMessage = $"EDGE: {name}: missing {analysis.SideName} segment CRC32={analysis.MissingCrc32Hex}, length={analysis.MissingLength:N0}.";
                activity?.Report(analysisMessage);
                messages.Add(analysisMessage);
            }

            if (findEnds.Found && !string.IsNullOrWhiteSpace(findEnds.OutputPath))
            {
                string fixedSource = $"EDGE FIXED: {name}: missing {side} segment found at source offset {findEnds.SourceOffset:N0}; reconstructed CRC32/MD5 verified.";
                activity?.Report(fixedSource);
                messages.Add(fixedSource);
                TryDelete(partialTemp);
                return new SearchResult(target, expectedStart, true, fixedSource, findEnds.CrcCandidates, findEnds.OutputPath);
            }

            if (savePartialOnFailure)
            {
                long inspectionOffset = partialTrim == AudioPartialTrim.None ? expectedStart : partialOffset;
                long inspectionLength = partialTrim == AudioPartialTrim.None ? target.Size : partialLength;
                SearchResult inspected = await SaveInspectionPartialAsync(
                    source, inspectionOffset, inspectionLength, target, targetIndex, outputRoot,
                    new SearchResult(target, expectedStart, false, $"EDGE: {name}: missing {side} segment was not recovered.", findEnds.CrcCandidates),
                    "target-sized window derived from the adjacent audio anchor",
                    activity, messages, cancellationToken, partialTrim).ConfigureAwait(false);
                return inspected;
            }

            string notRecovered = $"EDGE: {name}: missing {side} segment was not recovered.";
            Report(activity, messages, notRecovered);
            return new SearchResult(target, expectedStart, false, notRecovered, findEnds.CrcCandidates);
        }
        finally
        {
            TryDelete(partialTemp);
            TryDelete(zeroCandidate);
        }
    }

    private static async Task<bool> SaveInspectionPartialVariantAsync(
        string source,
        long offset,
        HashTarget target,
        int targetIndex,
        string outputRoot,
        string variant,
        string boundaryDescription,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        long sourceLength = new FileInfo(source).Length;
        if (target.Size <= 0 || offset < 0 || offset >= sourceLength)
        {
            Report(activity, messages,
                $"EDGE PARTIAL ({variant}): {TargetDisplayName(target, targetIndex)}: cannot save the target-sized hypothesis from source offset {offset:N0}; the start is outside the available source.");
            return false;
        }

        long available = sourceLength - offset;
        long saveLength = Math.Min(target.Size, available);
        if (saveLength <= 0)
            return false;

        string output = await SavePartialAsync(
            source, offset, saveLength, target, targetIndex, outputRoot, cancellationToken, variant).ConfigureAwait(false);

        if (saveLength == target.Size)
        {
            Report(activity, messages,
                $"EDGE PARTIAL ({variant}): {TargetDisplayName(target, targetIndex)}: saved exactly {target.Size:N0} byte(s) from source offset {offset:N0} as {Path.GetFileName(output)}; {boundaryDescription}.");
        }
        else
        {
            long shortfall = target.Size - saveLength;
            Report(activity, messages,
                $"EDGE PARTIAL ({variant}): {TargetDisplayName(target, targetIndex)}: saved {saveLength:N0} available byte(s) from source offset {offset:N0} as {Path.GetFileName(output)}; SHORT by {shortfall:N0} byte(s); {boundaryDescription}.");
        }

        return true;
    }

    private static async Task<SearchResult> SaveInspectionPartialAsync(
        string source,
        long offset,
        long length,
        HashTarget target,
        int targetIndex,
        string outputRoot,
        SearchResult existing,
        string boundaryDescription,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken,
        AudioPartialTrim trim = AudioPartialTrim.None)
    {
        // Prefer a target-sized inspection candidate.  If the requested window
        // starts inside the source but runs beyond EOF (the common final-track
        // under-dump case), still save every available byte and explicitly report
        // the shortfall instead of discarding the useful partial.
        long requestedLength = trim == AudioPartialTrim.None
            ? target.Size
            : Math.Min(length, target.Size);
        long sourceLength = new FileInfo(source).Length;
        if (requestedLength <= 0 || offset < 0 || offset >= sourceLength)
        {
            string unavailable =
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: cannot save an inspection window from source offset {offset:N0}; that start is outside the available source (target expects {target.Size:N0} byte(s)).";
            Report(activity, messages, unavailable);
            return existing;
        }

        long available = sourceLength - offset;
        long saveLength = Math.Min(requestedLength, available);
        if (saveLength <= 0)
        {
            string unavailable =
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: no source bytes are available at offset {offset:N0}.";
            Report(activity, messages, unavailable);
            return existing;
        }

        string output = await SavePartialAsync(
            source, offset, saveLength, target, targetIndex, outputRoot, cancellationToken).ConfigureAwait(false);

        if (trim != AudioPartialTrim.None)
        {
            await SaveZeroTrimmedAudioPartialAsync(
                source, offset, saveLength, target, targetIndex, outputRoot, trim,
                activity, messages, cancellationToken).ConfigureAwait(false);
        }

        string message;
        if (saveLength == target.Size)
        {
            message =
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: saved exactly the expected {target.Size:N0} byte(s) from source offset {offset:N0} as {Path.GetFileName(output)} for manual inspection; {boundaryDescription}.";
        }
        else
        {
            long shortfall = target.Size - saveLength;
            message =
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: saved the available {saveLength:N0} byte(s) from source offset {offset:N0} as {Path.GetFileName(output)} for manual inspection; the partial is SHORT by {shortfall:N0} byte(s) (target expects {target.Size:N0}); {boundaryDescription}.";
        }

        Report(activity, messages, message);
        return existing with { Status = message, OutputPath = output };
    }

    private static async Task SaveZeroTrimmedAudioPartialAsync(
        string source,
        long offset,
        long length,
        HashTarget target,
        int targetIndex,
        string outputRoot,
        AudioPartialTrim trim,
        IProgress<string>? activity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string side = trim == AudioPartialTrim.LeadingZeroFrames ? "leading" : "trailing";
        if (length <= 0 || length % CddaPcmFrameBytes != 0)
        {
            Report(activity, messages,
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: the {side}-zero-trimmed copy was not saved because the available audio is not aligned to a complete 4-byte stereo PCM frame.");
            return;
        }

        (long first, long last) = await FindNonZeroPcmFrameBoundsAsync(
            source, offset, length, cancellationToken).ConfigureAwait(false);
        if (first < 0)
        {
            Report(activity, messages,
                $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: the {side}-zero-trimmed copy was not saved because the partial contains only zero PCM frames.");
            return;
        }

        long trimmedOffset;
        long trimmedLength;
        long removedBytes;
        string variant;
        if (trim == AudioPartialTrim.LeadingZeroFrames)
        {
            removedBytes = first;
            trimmedOffset = checked(offset + removedBytes);
            trimmedLength = length - removedBytes;
            variant = "leading-zero-trimmed";
        }
        else
        {
            trimmedOffset = offset;
            trimmedLength = checked(last + CddaPcmFrameBytes);
            removedBytes = length - trimmedLength;
            variant = "trailing-zero-trimmed";
        }

        string output = await SavePartialAsync(
            source, trimmedOffset, trimmedLength, target, targetIndex, outputRoot,
            cancellationToken, variant).ConfigureAwait(false);
        Report(activity, messages,
            $"EDGE PARTIAL: {TargetDisplayName(target, targetIndex)}: saved {Path.GetFileName(output)} after removing {removedBytes:N0} byte(s) ({removedBytes / CddaPcmFrameBytes:N0} stereo PCM frame(s)) of {side} zero audio.");
    }

    private static async Task<(long First, long Last)> FindNonZeroPcmFrameBoundsAsync(
        string source,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = offset;
            long first = -1;
            long last = -1;
            long processed = 0;
            while (processed < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length - (buffer.Length % CddaPcmFrameBytes), length - processed);
                await input.ReadExactlyAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < want; i += CddaPcmFrameBytes)
                {
                    if (buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 0 && buffer[i + 3] == 0)
                        continue;

                    long frameOffset = processed + i;
                    if (first < 0)
                        first = frameOffset;
                    last = frameOffset;
                }
                processed += want;
            }
            return (first, last);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> SavePartialAsync(
        string source,
        long offset,
        long length,
        HashTarget target,
        int targetIndex,
        string outputRoot,
        CancellationToken cancellationToken,
        string? variant = null)
    {
        string output = GetPartialOutputPath(target, targetIndex, outputRoot, source, variant);
        string temp = output + ".tmp";
        try
        {
            await CopyRangeAsync(source, offset, length, temp, cancellationToken).ConfigureAwait(false);
            File.Move(temp, output, true);
            return output;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task CopyRangeAsync(string source, long offset, long length, string output, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dest = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = offset;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of source while extracting an edge partial.");
                await dest.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                remaining -= got;
            }
            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task BuildZeroCandidateAsync(
        string partialFile,
        string output,
        long missingLength,
        FindEndsMode mode,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        Array.Clear(zeros, 0, zeros.Length);
        try
        {
            await using var dest = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (mode == FindEndsMode.MissingStart)
                await WriteZerosAsync(dest, missingLength, zeros, cancellationToken).ConfigureAwait(false);

            await using (var input = new FileStream(partialFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int got;
                while ((got = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                    await dest.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            }

            if (mode == FindEndsMode.MissingEnd)
                await WriteZerosAsync(dest, missingLength, zeros, cancellationToken).ConfigureAwait(false);

            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static async Task WriteZerosAsync(Stream output, long length, byte[] zeros, CancellationToken cancellationToken)
    {
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(zeros.Length, remaining);
            await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static bool VerifyFile(string filePath, HashTarget target, CancellationToken cancellationToken, out string md5Hex)
    {
        if (new FileInfo(filePath).Length != target.Size)
        {
            md5Hex = string.Empty;
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        uint crc = 0;
        try
        {
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);
            int got;
            while ((got = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc = Crc32.Compute(buffer.AsSpan(0, got), crc);
                md5.AppendData(buffer, 0, got);
            }
            md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            string? expectedMd5 = target.NormalizedMd5;
            return crc == target.Crc32 &&
                   (expectedMd5 is null || expectedMd5.Equals(md5Hex, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GetRecoveredOutputPath(string source, HashTarget target, int targetIndex, string outputRoot)
    {
        string? targetOutputFileName = target.OutputFileName;
        string fileName = string.IsNullOrWhiteSpace(targetOutputFileName)
            ? $"Track_{targetIndex + 1:00}_{target.NormalizedMd5 ?? target.Crc32Hex}.bin"
            : Path.GetFileName(targetOutputFileName);
        string output = Path.Combine(outputRoot, fileName);
        if (PathsEqual(output, source))
            output = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(fileName) + "_recovered" + Path.GetExtension(fileName));
        return output;
    }

    private static string GetPartialOutputPath(HashTarget target, int targetIndex, string outputRoot, string? source = null, string? variant = null)
    {
        string? targetOutputFileName = target.OutputFileName;
        string baseName = string.IsNullOrWhiteSpace(targetOutputFileName)
            ? $"Track_{targetIndex + 1:00}_{target.NormalizedMd5 ?? target.Crc32Hex}"
            : Path.GetFileNameWithoutExtension(targetOutputFileName);
        string suffix = string.IsNullOrWhiteSpace(variant) ? ".partial" : $".{variant}.partial";
        string output = Path.Combine(outputRoot, baseName + suffix);
        if (source is not null && PathsEqual(output, source))
            output = Path.Combine(outputRoot, baseName + "_recovered" + suffix);
        return output;
    }

    private static string TargetDisplayName(HashTarget target, int index) =>
        string.IsNullOrWhiteSpace(target.Label) ? $"target {index + 1}" : target.Label;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Report(IProgress<string>? activity, List<string> messages, string message)
    {
        activity?.Report(message);
        messages.Add(message);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
