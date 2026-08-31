using System.Buffers;

namespace DumpToolbox.Core;

public sealed record ConcatenateProgress(
    long BytesWritten,
    long TotalBytes,
    int CurrentFileIndex,
    int FileCount,
    string CurrentFilePath,
    long CurrentFileBytesWritten,
    long CurrentFileLength)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesWritten / TotalBytes, 0, 1);
}

public sealed record ConcatenateOptions(
    long PaddingBytes = 0,
    bool CheckPaddingBoundaries = true,
    int BoundaryProbeBytes = 4096,
    int MinimumZeroRunBytes = 256)
{
    public bool PaddingEnabled => PaddingBytes > 0;
}

public sealed record ConcatenateBoundaryDecision(
    string PreviousFile,
    string NextFile,
    long PaddingBytes,
    bool ApplyPadding,
    int PreviousTrailingZeroBytes,
    int NextLeadingZeroBytes,
    string Reason);

public sealed record ConcatenateResult(
    string DestinationPath,
    int FilesProcessed,
    long BytesWritten,
    long PaddingBytesWritten,
    int PaddingBoundariesApplied,
    int PaddingBoundariesSkipped);

public sealed class ConcatenateService
{
    private const int BufferSize = 4 * 1024 * 1024;

    public async Task<ConcatenateResult> ConcatenateAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationPath,
        ConcatenateOptions? options = null,
        IProgress<ConcatenateProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ConcatenateOptions();

        if (sourcePaths.Count == 0)
            throw new ArgumentException("Add at least one source file.", nameof(sourcePaths));

        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Choose a destination filename.", nameof(destinationPath));

        if (options.PaddingBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Padding byte count cannot be negative.");

        if (options.BoundaryProbeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Boundary probe size must be greater than zero.");

        if (options.MinimumZeroRunBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum zero run must be greater than zero.");

        string[] sources = sourcePaths
            .Select(Path.GetFullPath)
            .ToArray();

        string destination = Path.GetFullPath(destinationPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (string source in sources)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("Source file not found.", source);

            if (source.Equals(destination, pathComparison))
                throw new InvalidOperationException("The destination file cannot also be one of the source files.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string partialPath = destination + ".partial";
        foreach (string source in sources)
        {
            if (source.Equals(partialPath, pathComparison))
                throw new InvalidOperationException("A source file conflicts with the temporary output filename.");
        }

        long[] lengths = new long[sources.Length];
        long sourceBytes = 0;
        for (int i = 0; i < sources.Length; i++)
        {
            lengths[i] = new FileInfo(sources[i]).Length;
            sourceBytes = checked(sourceBytes + lengths[i]);
        }

        ConcatenateBoundaryDecision[] boundaryDecisions = options.PaddingEnabled && sources.Length > 1
            ? await AnalyzeBoundariesAsync(sources, lengths, options, activity, cancellationToken)
            : Array.Empty<ConcatenateBoundaryDecision>();

        long paddingBytesPlanned = boundaryDecisions
            .Where(d => d.ApplyPadding)
            .Sum(d => d.PaddingBytes);

        long totalBytes = checked(sourceBytes + paddingBytesPlanned);

        if (File.Exists(partialPath))
            File.Delete(partialPath);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long totalWritten = 0;
        long paddingWritten = 0;
        int paddingApplied = 0;
        int paddingSkipped = boundaryDecisions.Count(d => !d.ApplyPadding);

        try
        {
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (int fileIndex = 0; fileIndex < sources.Length; fileIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string sourcePath = sources[fileIndex];
                    long currentLength = lengths[fileIndex];
                    long currentWritten = 0;

                    progress?.Report(new ConcatenateProgress(
                        totalWritten, totalBytes, fileIndex, sources.Length,
                        sourcePath, currentWritten, currentLength));

                    await using (var input = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                            if (read == 0)
                                break;

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            currentWritten += read;
                            totalWritten += read;

                            progress?.Report(new ConcatenateProgress(
                                totalWritten, totalBytes, fileIndex, sources.Length,
                                sourcePath, currentWritten, currentLength));
                        }
                    }

                    if (fileIndex < boundaryDecisions.Length)
                    {
                        ConcatenateBoundaryDecision decision = boundaryDecisions[fileIndex];
                        if (decision.ApplyPadding && decision.PaddingBytes > 0)
                        {
                            activity?.Report(
                                $"Inserting {decision.PaddingBytes:N0} zero bytes between " +
                                $"{Path.GetFileName(decision.PreviousFile)} and {Path.GetFileName(decision.NextFile)}.");

                            Array.Clear(buffer, 0, buffer.Length);
                            long remaining = decision.PaddingBytes;
                            while (remaining > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int chunk = (int)Math.Min((long)buffer.Length, remaining);
                                await output.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken);
                                remaining -= chunk;
                                totalWritten += chunk;
                                paddingWritten += chunk;

                                progress?.Report(new ConcatenateProgress(
                                    totalWritten, totalBytes, fileIndex, sources.Length,
                                    sourcePath, currentLength, currentLength));
                            }

                            paddingApplied++;
                        }
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, destination, overwrite: true);

            return new ConcatenateResult(
                destination,
                sources.Length,
                totalWritten,
                paddingWritten,
                paddingApplied,
                paddingSkipped);
        }
        catch
        {
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch
            {
                // Preserve the original error if cleanup fails.
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<ConcatenateBoundaryDecision[]> AnalyzeBoundariesAsync(
        IReadOnlyList<string> sources,
        IReadOnlyList<long> lengths,
        ConcatenateOptions options,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        var decisions = new ConcatenateBoundaryDecision[sources.Count - 1];

        activity?.Report(
            options.CheckPaddingBoundaries
                ? $"Checking {decisions.Length:N0} file boundaries before adding zero padding..."
                : "Boundary safety check disabled: padding will be inserted between every source file.");

        for (int i = 0; i < decisions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string previous = sources[i];
            string next = sources[i + 1];

            if (!options.CheckPaddingBoundaries)
            {
                decisions[i] = new ConcatenateBoundaryDecision(
                    previous, next, options.PaddingBytes, true, 0, 0,
                    "Boundary safety check disabled.");
                continue;
            }

            int trailingZeros = await ReadEdgeZeroRunAsync(
                previous, lengths[i], fromStart: false, options.BoundaryProbeBytes, cancellationToken);
            int leadingZeros = await ReadEdgeZeroRunAsync(
                next, lengths[i + 1], fromStart: true, options.BoundaryProbeBytes, cancellationToken);

            int previousSampleLength = (int)Math.Min((long)options.BoundaryProbeBytes, lengths[i]);
            int nextSampleLength = (int)Math.Min((long)options.BoundaryProbeBytes, lengths[i + 1]);

            int previousRequired = Math.Min(options.MinimumZeroRunBytes, Math.Max(1, previousSampleLength));
            int nextRequired = Math.Min(options.MinimumZeroRunBytes, Math.Max(1, nextSampleLength));

            bool previousLooksZero = previousSampleLength > 0 && trailingZeros >= previousRequired;
            bool nextLooksZero = nextSampleLength > 0 && leadingZeros >= nextRequired;
            bool apply = previousLooksZero || nextLooksZero;

            string reason = apply
                ? "At least one adjacent edge is zero-filled."
                : "Both adjacent edges contain data; automatic padding skipped.";

            decisions[i] = new ConcatenateBoundaryDecision(
                previous,
                next,
                options.PaddingBytes,
                apply,
                trailingZeros,
                leadingZeros,
                reason);

            string action = apply ? $"PAD {options.PaddingBytes:N0} bytes" : "SKIP padding";
            activity?.Report(
                $"Boundary {i + 1}/{decisions.Length}: {Path.GetFileName(previous)} -> {Path.GetFileName(next)} | " +
                $"tail zero run={trailingZeros:N0} B, head zero run={leadingZeros:N0} B | {action}");
        }

        return decisions;
    }

    private static async Task<int> ReadEdgeZeroRunAsync(
        string path,
        long fileLength,
        bool fromStart,
        int probeBytes,
        CancellationToken cancellationToken)
    {
        if (fileLength <= 0)
            return 0;

        int count = (int)Math.Min((long)probeBytes, fileLength);
        byte[] sample = ArrayPool<byte>.Shared.Rent(count);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                Math.Min(count, 64 * 1024),
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            stream.Position = fromStart ? 0 : fileLength - count;

            int readTotal = 0;
            while (readTotal < count)
            {
                int read = await stream.ReadAsync(sample.AsMemory(readTotal, count - readTotal), cancellationToken);
                if (read == 0)
                    break;
                readTotal += read;
            }

            if (readTotal == 0)
                return 0;

            int zeroRun = 0;
            if (fromStart)
            {
                for (int i = 0; i < readTotal && sample[i] == 0; i++)
                    zeroRun++;
            }
            else
            {
                for (int i = readTotal - 1; i >= 0 && sample[i] == 0; i--)
                    zeroRun++;
            }

            return zeroRun;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sample);
        }
    }
}
