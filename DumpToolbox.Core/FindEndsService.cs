using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public enum FindEndsMode
{
    Auto,
    MissingStart,
    MissingEnd
}

public sealed record FindEndsAnalysis(
    FindEndsMode Mode,
    long MissingLength,
    uint MissingCrc32)
{
    public string SideName => Mode == FindEndsMode.MissingStart ? "start" : "end";
    public string MissingCrc32Hex => MissingCrc32.ToString("x8");
}

public sealed record FindEndsProgress(
    long Offset,
    long SearchableOffsets,
    long CrcCandidates,
    string Message)
{
    public double Fraction => SearchableOffsets <= 0
        ? 0
        : Math.Clamp(Offset / (double)SearchableOffsets, 0, 1);
}

public sealed record FindEndsResult(
    string PartialFile,
    long PartialLength,
    uint PartialCrc32,
    long TargetLength,
    uint TargetCrc32,
    string TargetMd5,
    IReadOnlyList<FindEndsAnalysis> Analyses,
    bool SourceSearched,
    bool Found,
    FindEndsMode? MatchedMode = null,
    long? SourceOffset = null,
    string? OutputPath = null,
    string? VerifiedMd5 = null,
    long CrcCandidates = 0,
    string? Message = null);

/// <summary>
/// Reconstructs a file that is missing a contiguous prefix or suffix. The missing
/// segment CRC is derived from the complete CRC and the partial file, then an
/// optional source file is searched with a rolling CRC32. CRC hits are accepted
/// only when the reconstructed complete file matches the supplied MD5.
/// </summary>
public sealed partial class FindEndsService
{
    private const int BufferSize = 4 * 1024 * 1024;

    public Task<FindEndsResult> RunAsync(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        string? sourceFile = null,
        string? outputFile = null,
        IProgress<FindEndsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partialFile))
            throw new ArgumentException("A partial file is required.", nameof(partialFile));
        if (!File.Exists(partialFile))
            throw new FileNotFoundException("Partial file not found.", partialFile);
        if (targetLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetLength), "Full length must be greater than zero.");

        string normalizedMd5 = NormalizeMd5(targetMd5);
        string partial = Path.GetFullPath(partialFile);
        string? source = string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFullPath(sourceFile);
        string? output = string.IsNullOrWhiteSpace(outputFile) ? null : Path.GetFullPath(outputFile);

        if (source is not null && !File.Exists(source))
            throw new FileNotFoundException("Source file not found.", source);

        if (output is not null)
        {
            if (PathsEqual(output, partial))
                throw new InvalidOperationException("Recovered output must not overwrite the partial input file.");
            if (source is not null && PathsEqual(output, source))
                throw new InvalidOperationException("Recovered output must not overwrite the source search file.");
        }

        return Task.Run(
            () => RunCore(partial, targetLength, targetCrc32, normalizedMd5, mode, source, output, progress, cancellationToken),
            cancellationToken);
    }

    private static FindEndsResult RunCore(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        string? sourceFile,
        string? outputFile,
        IProgress<FindEndsProgress>? progress,
        CancellationToken cancellationToken)
    {
        long partialLength = new FileInfo(partialFile).Length;
        if (partialLength >= targetLength)
            throw new InvalidOperationException($"Partial file must be smaller than the full length ({partialLength:N0} >= {targetLength:N0}).");

        long missingLength = targetLength - partialLength;
        progress?.Report(new FindEndsProgress(0, 0, 0, "Calculating partial-file CRC32..."));
        uint partialCrc = ComputeFileCrc32(partialFile, cancellationToken);

        var analyses = new List<FindEndsAnalysis>(2);
        if (mode is FindEndsMode.Auto or FindEndsMode.MissingStart)
        {
            // Complete = missing-prefix || partial-suffix.
            // target = Shift(partialLength, prefixCRC) XOR partialCRC.
            uint prefixCrc = Crc32.CreateInverseShiftOperator(partialLength)
                .Apply(targetCrc32 ^ partialCrc);
            analyses.Add(new FindEndsAnalysis(FindEndsMode.MissingStart, missingLength, prefixCrc));
        }

        if (mode is FindEndsMode.Auto or FindEndsMode.MissingEnd)
        {
            // Complete = partial-prefix || missing-suffix.
            // target = Shift(missingLength, partialCRC) XOR suffixCRC.
            uint suffixCrc = targetCrc32 ^ Crc32.CreateShiftOperator(missingLength).Apply(partialCrc);
            analyses.Add(new FindEndsAnalysis(FindEndsMode.MissingEnd, missingLength, suffixCrc));
        }

        foreach (FindEndsAnalysis analysis in analyses)
        {
            progress?.Report(new FindEndsProgress(0, 0, 0,
                $"Need {analysis.MissingLength:N0} bytes at the {analysis.SideName} with CRC32 {analysis.MissingCrc32Hex}"));
        }

        if (sourceFile is null)
        {
            return new FindEndsResult(
                partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                analyses, SourceSearched: false, Found: false,
                Message: "Missing segment CRC32 calculated. Supply a source file to search for it.");
        }

        string sourcePath = sourceFile;
        long sourceLength = new FileInfo(sourcePath).Length;
        if (sourceLength < missingLength)
        {
            return new FindEndsResult(
                partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                analyses, SourceSearched: true, Found: false,
                Message: $"Source file is smaller than the missing segment ({sourceLength:N0} < {missingLength:N0} bytes)." );
        }

        string finalOutput = outputFile ?? SuggestOutputPath(partialFile);
        if (PathsEqual(finalOutput, partialFile) || PathsEqual(finalOutput, sourcePath))
            throw new InvalidOperationException("Recovered output must be different from the partial and source files.");

        var analysesByCrc = analyses
            .GroupBy(a => a.MissingCrc32)
            .ToDictionary(g => g.Key, g => g.ToArray());

        long maxOffset = sourceLength - missingLength;
        long candidateCount = 0;
        progress?.Report(new FindEndsProgress(0, Math.Max(1, maxOffset), 0,
            $"Searching {sourceLength:N0} bytes for a {missingLength:N0}-byte missing segment..."));

        uint currentCrc;
        using var incoming = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.SequentialScan);
        using var outgoing = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.SequentialScan);

        byte[] initialBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            currentCrc = 0;
            long remainingInitial = missingLength;
            while (remainingInitial > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(initialBuffer.Length, remainingInitial);
                int got = incoming.Read(initialBuffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of source while reading the first search window.");
                currentCrc = Crc32.Compute(initialBuffer.AsSpan(0, got), currentCrc);
                remainingInitial -= got;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(initialBuffer);
        }

        var shiftTail = Crc32.CreateShiftOperator(missingLength - 1);
        var shiftOne = Crc32.CreateShiftOperator(1);
        uint[][] shiftOneTables = shiftOne.CreateByteTables();
        var outgoingContribution = new uint[256];
        var oneByteCrc = new uint[256];
        Span<byte> oneByte = stackalloc byte[1];
        for (int value = 0; value < 256; value++)
        {
            oneByte[0] = (byte)value;
            uint crc = Crc32.Compute(oneByte);
            oneByteCrc[value] = crc;
            outgoingContribution[value] = shiftTail.Apply(crc);
        }

        bool TryCurrentWindow(long offset, out FindEndsResult? result)
        {
            result = null;
            if (!analysesByCrc.TryGetValue(currentCrc, out FindEndsAnalysis[]? possibleModes) || possibleModes is null)
                return false;

            candidateCount++;
            foreach (FindEndsAnalysis analysis in possibleModes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string actualMd5 = ComputeReconstructedMd5(
                    partialFile, sourcePath, offset, missingLength, analysis.Mode, cancellationToken);

                progress?.Report(new FindEndsProgress(offset, Math.Max(1, maxOffset), candidateCount,
                    $"CRC32 candidate at {offset:N0} for missing {analysis.SideName}; MD5 {actualMd5}"));

                if (!actualMd5.Equals(targetMd5, StringComparison.OrdinalIgnoreCase))
                    continue;

                string writtenMd5 = WriteReconstructedFile(
                    partialFile, sourcePath, offset, missingLength, analysis.Mode,
                    finalOutput, targetMd5, cancellationToken);

                result = new FindEndsResult(
                    partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                    analyses, SourceSearched: true, Found: true,
                    MatchedMode: analysis.Mode,
                    SourceOffset: offset,
                    OutputPath: finalOutput,
                    VerifiedMd5: writtenMd5,
                    CrcCandidates: candidateCount,
                    Message: $"Recovered missing {analysis.SideName} from source offset {offset:N0}." );
                return true;
            }

            return false;
        }

        if (TryCurrentWindow(0, out FindEndsResult? initialMatch))
            return initialMatch!;

        if (maxOffset > 0)
        {
            byte[] incomingBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            byte[] outgoingBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long offset = 0;
                long remainingSlides = maxOffset;
                long nextProgress = Math.Min(maxOffset, 16L * 1024 * 1024);

                while (remainingSlides > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(BufferSize, remainingSlides);
                    int gotIncoming = ReadSome(incoming, incomingBuffer, want);
                    int gotOutgoing = ReadSome(outgoing, outgoingBuffer, want);
                    int got = Math.Min(gotIncoming, gotOutgoing);
                    if (got <= 0)
                        break;

                    for (int i = 0; i < got; i++)
                    {
                        uint remainder = currentCrc ^ outgoingContribution[outgoingBuffer[i]];
                        currentCrc = Crc32.ShiftOperator.ApplyByteTables(shiftOneTables, remainder)
                                   ^ oneByteCrc[incomingBuffer[i]];
                        offset++;

                        if (TryCurrentWindow(offset, out FindEndsResult? match))
                            return match!;

                        if (offset >= nextProgress)
                        {
                            progress?.Report(new FindEndsProgress(offset, maxOffset, candidateCount,
                                $"Searching source at offset {offset:N0}/{maxOffset:N0}..."));
                            nextProgress = Math.Min(maxOffset, offset + 16L * 1024 * 1024);
                        }
                    }

                    remainingSlides -= got;
                    if (got < want)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(incomingBuffer);
                ArrayPool<byte>.Shared.Return(outgoingBuffer);
            }
        }

        progress?.Report(new FindEndsProgress(maxOffset, Math.Max(1, maxOffset), candidateCount, "Search complete — no MD5-verified match."));
        return new FindEndsResult(
            partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
            analyses, SourceSearched: true, Found: false,
            CrcCandidates: candidateCount,
            Message: candidateCount == 0
                ? "No source block matched the calculated missing CRC32."
                : $"{candidateCount:N0} CRC32 candidate(s) were found, but none reconstructed to the expected MD5.");
    }


    /// <summary>
    /// Heads-and-Tails edge recovery for an exact-length audio edge whose known digital
    /// silence was removed before searching. The caller may force zero bytes either
    /// at the physical outer edge or at the inner boundary next to the anchored
    /// audio. The source segment occupies the remaining missing bytes.
    ///
    /// MissingStart: outer-zeros || source || inner-zeros || partial
    /// MissingEnd:   partial || inner-zeros || source || outer-zeros
    /// </summary>
    public Task<FindEndsResult> RunWithFixedBoundaryZerosAsync(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        long fixedOuterZeroBytes,
        long fixedInnerZeroBytes,
        string sourceFile,
        string outputFile,
        uint? knownPartialCrc32 = null,
        uint? knownOuterZeroCrc32 = null,
        uint? knownInnerZeroCrc32 = null,
        IProgress<FindEndsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == FindEndsMode.Auto)
            throw new ArgumentException("Heads-and-Tails fixed-zero recovery requires MissingStart or MissingEnd.", nameof(mode));
        if (fixedOuterZeroBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedOuterZeroBytes));
        if (fixedInnerZeroBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedInnerZeroBytes));
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

        return Task.Run(() => RunFixedBoundaryZerosCore(
            partial, targetLength, targetCrc32, normalizedMd5, mode,
            fixedOuterZeroBytes, fixedInnerZeroBytes, source, output,
            knownPartialCrc32, knownOuterZeroCrc32, knownInnerZeroCrc32,
            progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Compatibility wrapper for the original Heads-and-Tails outer-edge-only search.
    /// </summary>
    public Task<FindEndsResult> RunWithFixedOuterZerosAsync(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        long fixedOuterZeroBytes,
        string sourceFile,
        string outputFile,
        uint? knownPartialCrc32 = null,
        uint? knownFixedZeroCrc32 = null,
        IProgress<FindEndsProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => RunWithFixedBoundaryZerosAsync(
            partialFile, targetLength, targetCrc32, targetMd5, mode,
            fixedOuterZeroBytes, 0, sourceFile, outputFile,
            knownPartialCrc32, knownFixedZeroCrc32, 0,
            progress, cancellationToken);

    private static FindEndsResult RunFixedBoundaryZerosCore(
        string partialFile,
        long targetLength,
        uint targetCrc32,
        string targetMd5,
        FindEndsMode mode,
        long fixedOuterZeroBytes,
        long fixedInnerZeroBytes,
        string sourceFile,
        string outputFile,
        uint? knownPartialCrc32,
        uint? knownOuterZeroCrc32,
        uint? knownInnerZeroCrc32,
        IProgress<FindEndsProgress>? progress,
        CancellationToken cancellationToken)
    {
        long partialLength = new FileInfo(partialFile).Length;
        long totalMissing = targetLength - partialLength;
        if (totalMissing <= 0)
            throw new InvalidOperationException("Partial file must be smaller than the full length.");
        if (fixedOuterZeroBytes + fixedInnerZeroBytes > totalMissing)
            throw new ArgumentOutOfRangeException(nameof(fixedOuterZeroBytes), "Fixed zero bytes exceed the missing edge length.");

        long searchableLength = totalMissing - fixedOuterZeroBytes - fixedInnerZeroBytes;
        uint partialCrc = knownPartialCrc32 ?? ComputeFileCrc32(partialFile, cancellationToken);
        uint outerZeroCrc = knownOuterZeroCrc32 ?? ComputeZeroCrc32(fixedOuterZeroBytes, cancellationToken);
        uint innerZeroCrc = knownInnerZeroCrc32 ?? ComputeZeroCrc32(fixedInnerZeroBytes, cancellationToken);
        uint searchableCrc;

        if (mode == FindEndsMode.MissingEnd)
        {
            // target = partial || inner-zeros || searchable || outer-zeros
            uint beforeOuter = fixedOuterZeroBytes == 0
                ? targetCrc32
                : Crc32.CreateInverseShiftOperator(fixedOuterZeroBytes).Apply(targetCrc32 ^ outerZeroCrc);

            uint partialAndInner = fixedInnerZeroBytes == 0
                ? partialCrc
                : Crc32.CreateShiftOperator(fixedInnerZeroBytes).Apply(partialCrc) ^ innerZeroCrc;

            searchableCrc = beforeOuter ^ Crc32.CreateShiftOperator(searchableLength).Apply(partialAndInner);
        }
        else
        {
            // target = outer-zeros || searchable || inner-zeros || partial
            uint beforePartial = Crc32.CreateInverseShiftOperator(partialLength).Apply(targetCrc32 ^ partialCrc);
            uint beforeInner = fixedInnerZeroBytes == 0
                ? beforePartial
                : Crc32.CreateInverseShiftOperator(fixedInnerZeroBytes).Apply(beforePartial ^ innerZeroCrc);

            searchableCrc = beforeInner ^ Crc32.CreateShiftOperator(searchableLength).Apply(outerZeroCrc);
        }

        var analysis = new FindEndsAnalysis(mode, searchableLength, searchableCrc);
        var analyses = new[] { analysis };
        progress?.Report(new FindEndsProgress(0, 0, 0,
            $"Heads and Tails: {fixedInnerZeroBytes:N0} inner + {fixedOuterZeroBytes:N0} outer fixed zero byte(s); searching {searchableLength:N0} source byte(s) with CRC32 {searchableCrc:x8}."));

        if (searchableLength == 0)
        {
            string md5 = ComputeReconstructedMd5WithBoundaryZeros(
                partialFile, sourceFile, 0, 0, mode,
                fixedOuterZeroBytes, fixedInnerZeroBytes, cancellationToken);
            if (md5.Equals(targetMd5, StringComparison.OrdinalIgnoreCase))
            {
                string written = WriteReconstructedFileWithBoundaryZeros(
                    partialFile, sourceFile, 0, 0, mode,
                    fixedOuterZeroBytes, fixedInnerZeroBytes,
                    outputFile, targetMd5, cancellationToken);
                return new FindEndsResult(partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                    analyses, true, true, mode, 0, outputFile, written, 1,
                    $"Recovered with {fixedInnerZeroBytes:N0} inner + {fixedOuterZeroBytes:N0} outer fixed zero byte(s) and no source segment.");
            }

            return new FindEndsResult(partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                analyses, true, false, CrcCandidates: 1,
                Message: "All-zero Heads-and-Tails candidate did not match the expected MD5.");
        }

        long sourceLength = new FileInfo(sourceFile).Length;
        if (sourceLength < searchableLength)
        {
            return new FindEndsResult(partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                analyses, true, false,
                Message: $"Source file is smaller than the searchable segment ({sourceLength:N0} < {searchableLength:N0}).");
        }

        long maxOffset = sourceLength - searchableLength;
        long candidateCount = 0;
        uint currentCrc = 0;

        using var incoming = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        using var outgoing = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

        byte[] initialBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = searchableLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(initialBuffer.Length, remaining);
                int got = incoming.Read(initialBuffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of source while reading the first Heads-and-Tails search window.");
                currentCrc = Crc32.Compute(initialBuffer.AsSpan(0, got), currentCrc);
                remaining -= got;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(initialBuffer);
        }

        var shiftTail = Crc32.CreateShiftOperator(searchableLength - 1);
        var shiftOne = Crc32.CreateShiftOperator(1);
        uint[][] shiftOneTables = shiftOne.CreateByteTables();
        var outgoingContribution = new uint[256];
        var oneByteCrc = new uint[256];
        Span<byte> oneByte = stackalloc byte[1];
        for (int value = 0; value < 256; value++)
        {
            oneByte[0] = (byte)value;
            uint crc = Crc32.Compute(oneByte);
            oneByteCrc[value] = crc;
            outgoingContribution[value] = shiftTail.Apply(crc);
        }

        bool TryWindow(long offset, out FindEndsResult? match)
        {
            match = null;
            if (currentCrc != searchableCrc)
                return false;

            candidateCount++;
            string actualMd5 = ComputeReconstructedMd5WithBoundaryZeros(
                partialFile, sourceFile, offset, searchableLength, mode,
                fixedOuterZeroBytes, fixedInnerZeroBytes, cancellationToken);
            if (!actualMd5.Equals(targetMd5, StringComparison.OrdinalIgnoreCase))
                return false;

            string written = WriteReconstructedFileWithBoundaryZeros(
                partialFile, sourceFile, offset, searchableLength, mode,
                fixedOuterZeroBytes, fixedInnerZeroBytes,
                outputFile, targetMd5, cancellationToken);
            match = new FindEndsResult(partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
                analyses, true, true, mode, offset, outputFile, written, candidateCount,
                $"Recovered from source offset {offset:N0} with {fixedInnerZeroBytes:N0} inner + {fixedOuterZeroBytes:N0} outer fixed zero byte(s).");
            return true;
        }

        if (TryWindow(0, out FindEndsResult? initial))
            return initial!;

        if (maxOffset > 0)
        {
            byte[] incomingBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            byte[] outgoingBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long offset = 0;
                long remainingSlides = maxOffset;
                long nextProgress = Math.Min(maxOffset, 32L * 1024 * 1024);
                while (remainingSlides > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(BufferSize, remainingSlides);
                    int gotIncoming = ReadSome(incoming, incomingBuffer, want);
                    int gotOutgoing = ReadSome(outgoing, outgoingBuffer, want);
                    int got = Math.Min(gotIncoming, gotOutgoing);
                    if (got <= 0)
                        break;

                    for (int i = 0; i < got; i++)
                    {
                        uint remainder = currentCrc ^ outgoingContribution[outgoingBuffer[i]];
                        currentCrc = Crc32.ShiftOperator.ApplyByteTables(shiftOneTables, remainder)
                                   ^ oneByteCrc[incomingBuffer[i]];
                        offset++;
                        if (TryWindow(offset, out FindEndsResult? found))
                            return found!;
                        if (offset >= nextProgress)
                        {
                            progress?.Report(new FindEndsProgress(offset, maxOffset, candidateCount,
                                $"Heads and Tails search {offset:N0}/{maxOffset:N0}; {fixedInnerZeroBytes:N0} inner + {fixedOuterZeroBytes:N0} outer zero byte(s)."));
                            nextProgress = Math.Min(maxOffset, offset + 32L * 1024 * 1024);
                        }
                    }
                    remainingSlides -= got;
                    if (got < want)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(incomingBuffer);
                ArrayPool<byte>.Shared.Return(outgoingBuffer);
            }
        }

        return new FindEndsResult(partialFile, partialLength, partialCrc, targetLength, targetCrc32, targetMd5,
            analyses, true, false, CrcCandidates: candidateCount,
            Message: candidateCount == 0
                ? "No source block matched this Heads-and-Tails CRC32 split."
                : $"{candidateCount:N0} CRC32 candidate(s) were found for this split, but none matched MD5.");
    }

    private static uint ComputeFileCrc32(string path, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            uint crc = 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            int got;
            while ((got = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc = Crc32.Compute(buffer.AsSpan(0, got), crc);
            }
            return crc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ComputeReconstructedMd5(
        string partialFile,
        string sourceFile,
        long sourceOffset,
        long missingLength,
        FindEndsMode mode,
        CancellationToken cancellationToken)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (mode == FindEndsMode.MissingStart)
        {
            AppendFileRange(md5, sourceFile, sourceOffset, missingLength, cancellationToken);
            AppendWholeFile(md5, partialFile, cancellationToken);
        }
        else
        {
            AppendWholeFile(md5, partialFile, cancellationToken);
            AppendFileRange(md5, sourceFile, sourceOffset, missingLength, cancellationToken);
        }
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }


    private static uint ComputeZeroCrc32(long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
            return 0;
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Array.Clear(zeros, 0, zeros.Length);
            uint crc = 0;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zeros.Length, remaining);
                crc = Crc32.Compute(zeros.AsSpan(0, count), crc);
                remaining -= count;
            }
            return crc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static string ComputeReconstructedMd5WithBoundaryZeros(
        string partialFile,
        string sourceFile,
        long sourceOffset,
        long searchableLength,
        FindEndsMode mode,
        long fixedOuterZeroBytes,
        long fixedInnerZeroBytes,
        CancellationToken cancellationToken)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (mode == FindEndsMode.MissingStart)
        {
            AppendZeros(md5, fixedOuterZeroBytes, cancellationToken);
            if (searchableLength > 0)
                AppendFileRange(md5, sourceFile, sourceOffset, searchableLength, cancellationToken);
            AppendZeros(md5, fixedInnerZeroBytes, cancellationToken);
            AppendWholeFile(md5, partialFile, cancellationToken);
        }
        else
        {
            AppendWholeFile(md5, partialFile, cancellationToken);
            AppendZeros(md5, fixedInnerZeroBytes, cancellationToken);
            if (searchableLength > 0)
                AppendFileRange(md5, sourceFile, sourceOffset, searchableLength, cancellationToken);
            AppendZeros(md5, fixedOuterZeroBytes, cancellationToken);
        }
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendZeros(IncrementalHash hash, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
            return;
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Array.Clear(zeros, 0, zeros.Length);
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zeros.Length, remaining);
                hash.AppendData(zeros, 0, count);
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static string WriteReconstructedFileWithBoundaryZeros(
        string partialFile,
        string sourceFile,
        long sourceOffset,
        long searchableLength,
        FindEndsMode mode,
        long fixedOuterZeroBytes,
        long fixedInnerZeroBytes,
        string outputFile,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string temp = outputFile + ".partial";
        TryDelete(temp);
        byte[] zeros = ArrayPool<byte>.Shared.Rent(BufferSize);
        Array.Clear(zeros, 0, zeros.Length);
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            {
                if (mode == FindEndsMode.MissingStart)
                {
                    WriteZeros(output, fixedOuterZeroBytes, zeros, cancellationToken);
                    if (searchableLength > 0)
                        CopyRange(sourceFile, sourceOffset, searchableLength, output, cancellationToken);
                    WriteZeros(output, fixedInnerZeroBytes, zeros, cancellationToken);
                    CopyWholeFile(partialFile, output, cancellationToken);
                }
                else
                {
                    CopyWholeFile(partialFile, output, cancellationToken);
                    WriteZeros(output, fixedInnerZeroBytes, zeros, cancellationToken);
                    if (searchableLength > 0)
                        CopyRange(sourceFile, sourceOffset, searchableLength, output, cancellationToken);
                    WriteZeros(output, fixedOuterZeroBytes, zeros, cancellationToken);
                }
                output.Flush(true);
            }

            string writtenMd5 = ComputeFileMd5(temp, cancellationToken);
            if (!writtenMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovered Heads-and-Tails output did not match the expected MD5.");
            File.Move(temp, outputFile, overwrite: true);
            return writtenMd5;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static void WriteZeros(Stream output, long length, byte[] zeros, CancellationToken cancellationToken)
    {
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(zeros.Length, remaining);
            output.Write(zeros, 0, count);
            remaining -= count;
        }
    }

    private static string ComputeFileMd5(string path, CancellationToken cancellationToken)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        AppendWholeFile(md5, path, cancellationToken);
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendWholeFile(IncrementalHash hash, string path, CancellationToken cancellationToken)
    {
        long length = new FileInfo(path).Length;
        AppendFileRange(hash, path, 0, length, cancellationToken);
    }

    private static void AppendFileRange(
        IncrementalHash hash,
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            stream.Position = offset;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = stream.Read(buffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException($"Unexpected end of file while reading {Path.GetFileName(path)}.");
                hash.AppendData(buffer, 0, got);
                remaining -= got;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string WriteReconstructedFile(
        string partialFile,
        string sourceFile,
        long sourceOffset,
        long missingLength,
        FindEndsMode mode,
        string outputFile,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string temp = outputFile + ".partial";
        TryDelete(temp);

        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            {
                if (mode == FindEndsMode.MissingStart)
                {
                    CopyRange(sourceFile, sourceOffset, missingLength, output, cancellationToken);
                    CopyWholeFile(partialFile, output, cancellationToken);
                }
                else
                {
                    CopyWholeFile(partialFile, output, cancellationToken);
                    CopyRange(sourceFile, sourceOffset, missingLength, output, cancellationToken);
                }
                output.Flush(true);
            }

            // Verify the completed temporary file before it is allowed to replace any
            // existing output. This also guarantees the on-disk reconstruction, not only
            // the virtual candidate order, matches the known complete MD5.
            string writtenMd5 = ComputeFileMd5(temp, cancellationToken);
            if (!writtenMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovered temporary output did not match the expected MD5.");

            File.Move(temp, outputFile, overwrite: true);
            return writtenMd5;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void CopyWholeFile(string path, Stream output, CancellationToken cancellationToken)
        => CopyRange(path, 0, new FileInfo(path).Length, output, cancellationToken);

    private static void CopyRange(
        string path,
        long offset,
        long length,
        Stream output,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            input.Position = offset;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = input.Read(buffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException($"Unexpected end of file while copying {Path.GetFileName(path)}.");
                output.Write(buffer, 0, got);
                remaining -= got;
            }
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

    private static string NormalizeMd5(string md5)
    {
        string normalized = (md5 ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (normalized.Length != 32 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Full MD5 must contain exactly 32 hexadecimal digits.", nameof(md5));
        return normalized;
    }

    private static string SuggestOutputPath(string partialFile)
    {
        string directory = Path.GetDirectoryName(partialFile) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(partialFile);
        string ext = Path.GetExtension(partialFile);
        return Path.Combine(directory, stem + "_fixed" + ext);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original error/result if cleanup itself fails.
        }
    }
}
