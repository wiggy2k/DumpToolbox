using System.Buffers;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed record AudioRecoverySource(
    string SourcePath,
    LosslessAudioInfo Info,
    string ConvertedBinPath,
    long ConvertedBytes,
    long CombinedStartOffset,
    bool IsDirectRawSource = false);

public sealed record AudioRecoveredTrack(
    HashTarget Target,
    bool Found,
    string? OutputPath,
    long? SearchOffset,
    long? CombinedOffset,
    long LeadingSilenceBytes,
    long TrailingSilenceBytes,
    long? NearestSourceBoundaryDeltaBytes,
    string Status);

public sealed record AudioRecoveryResult(
    string OutputDirectory,
    string CombinedBinPath,
    long CombinedBytes,
    long EdgePaddingBytes,
    IReadOnlyList<AudioRecoverySource> Sources,
    IReadOnlyList<AudioRecoveredTrack> Tracks);

public sealed record AudioRecoveryProgress(double Fraction, string Stage, string Message);

public sealed class AudioRecoveryService
{
    public static bool IsRawPcmSourcePath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".iso", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedAudioSourcePath(string path) =>
        IsRawPcmSourcePath(path) || LosslessAudioDecoder.IsSupportedSourcePath(path);
    public const int CddaSampleRate = 44100;
    public const int CddaBytesPerStereoFrame = 4;
    public const int CddaBytesPerSecond = CddaSampleRate * CddaBytesPerStereoFrame;
    public const int CddaSectorBytes = 2352;
    public const int CddaStereoFramesPerSector = 588;

    private const int BufferSize = 4 * 1024 * 1024;
    private readonly LosslessAudioDecoder _decoder = new();
    private readonly HashSearchEngine _searchEngine = new();
    private readonly EdgeRecoveryService _edgeRecoveryService = new();

    public Task<LosslessAudioInfo> InspectAudioAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsRawPcmSourcePath(path))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Raw audio/image source file not found.", path);
            long bytes = new FileInfo(path).Length;
            if (bytes % CddaBytesPerStereoFrame != 0)
                throw new InvalidDataException($"{Path.GetFileName(path)} is {bytes:N0} bytes, which is not aligned to a 4-byte CDDA stereo sample frame.");
            return Task.FromResult(new LosslessAudioInfo(
                Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                "raw_pcm_s16le", 44100, 2, 16, bytes / CddaBytesPerStereoFrame, true,
                "Direct raw byte stream (no conversion)"));
        }
        return _decoder.InspectAsync(path, cancellationToken);
    }

    public static IReadOnlyList<string> LoadPlaylist(string playlistPath)
    {
        if (!File.Exists(playlistPath))
            throw new FileNotFoundException("Playlist not found.", playlistPath);

        string extension = Path.GetExtension(playlistPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? Directory.GetCurrentDirectory();
        string[] lines = File.ReadAllLines(playlistPath);
        var paths = new List<string>();

        if (extension.Equals(".pls", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 4 || !line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddPlaylistPath(paths, directory, line[(equals + 1)..].Trim());
            }
        }
        else if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            var fileRegex = new Regex("^\\s*FILE\\s+(?:\\\"(?<q>[^\\\"]+)\\\"|(?<u>\\S+))", RegexOptions.IgnoreCase);
            foreach (string raw in lines)
            {
                Match match = fileRegex.Match(raw);
                if (!match.Success)
                    continue;
                string value = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["u"].Value;
                AddPlaylistPath(paths, directory, value);
            }
        }
        else
        {
            // M3U/M3U8 and simple one-path-per-line text lists.
            foreach (string raw in lines)
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                AddPlaylistPath(paths, directory, line);
            }
        }

        return paths;
    }

    public Task<AudioRecoveryResult> RecoverAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<HashTarget> targets,
        string outputDirectory,
        double edgeSilenceSeconds,
        bool attemptUnderdumpedEdgeRepair = false,
        bool saveEdgePartials = false,
        bool enableHeadsTails = false,
        string? headsTailsSourceFile = null,
        IProgress<AudioRecoveryProgress>? progress = null,
        IProgress<SearchProgress>? searchProgress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            if (sourcePaths.Count == 0)
                throw new InvalidOperationException("Add at least one lossless audio source.");
            if (targets.Count == 0)
                throw new InvalidOperationException("Paste at least one target track hash.");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Choose an output folder.");
            if (double.IsNaN(edgeSilenceSeconds) || double.IsInfinity(edgeSilenceSeconds) || edgeSilenceSeconds < 0 || edgeSilenceSeconds > 300)
                throw new InvalidOperationException("Edge silence search must be between 0 and 300 seconds.");

            string outputRoot = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            long edgeBytes = checked((long)Math.Round(edgeSilenceSeconds * CddaBytesPerSecond));
            edgeBytes -= edgeBytes % CddaBytesPerStereoFrame;

            var sources = new List<AudioRecoverySource>();
            long combinedOffset = 0;
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = Path.GetFullPath(sourcePaths[i]);
                LosslessAudioInfo info = await InspectAudioAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (!info.IsCddaCompatible)
                {
                    throw new InvalidDataException(
                        $"{Path.GetFileName(sourcePath)} is {info.SampleRate:N0} Hz / {info.BitsPerSample}-bit / {info.Channels} channel(s); " +
                        "CDDA recovery requires 44,100 Hz / 16-bit / stereo.");
                }

                bool directRaw = IsRawPcmSourcePath(sourcePath);
                string convertedPath;
                long bytes;
                if (directRaw)
                {
                    convertedPath = sourcePath;
                    bytes = new FileInfo(sourcePath).Length;
                    progress?.Report(new AudioRecoveryProgress(((i + 1d) / Math.Max(sourcePaths.Count, 1)) * 0.55, "Source",
                        $"{i + 1}/{sourcePaths.Count}: {Path.GetFileName(sourcePath)} — using raw BIN/ISO directly; no conversion"));
                }
                else
                {
                    string stem = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
                    convertedPath = Path.Combine(outputRoot, $"{i + 1:00}_{stem}.cdda.bin");
                    int sourceIndex = i;
                    var decodeProgress = new Progress<LosslessAudioDecodeProgress>(p =>
                    {
                        double local = p.TotalSamples <= 0 ? 0 : p.Fraction;
                        double fraction = (sourceIndex + local) / Math.Max(sourcePaths.Count, 1);
                        progress?.Report(new AudioRecoveryProgress(fraction * 0.55, "Decode",
                            $"{sourceIndex + 1}/{sourcePaths.Count}: {Path.GetFileName(sourcePath)} — {p.Message}"));
                    });

                    await _decoder.DecodeToCddaAsync(sourcePath, convertedPath, decodeProgress, cancellationToken).ConfigureAwait(false);
                    bytes = new FileInfo(convertedPath).Length;
                }
                sources.Add(new AudioRecoverySource(sourcePath, info, convertedPath, bytes, combinedOffset, directRaw));
                combinedOffset += bytes;
            }

            string combinedPath = Path.Combine(outputRoot, "combined_cdda.bin");
            progress?.Report(new AudioRecoveryProgress(0.56, "Concatenate", "Concatenating decoded CDDA streams..."));
            await ConcatenateAsync(sources.Select(s => s.ConvertedBinPath), combinedPath, cancellationToken).ConfigureAwait(false);
            long combinedBytes = new FileInfo(combinedPath).Length;

            string searchPath = Path.Combine(outputRoot, ".dumptoolbox_audio_search.bin");
            bool ownsSearchPath = edgeBytes > 0;
            if (edgeBytes > 0)
            {
                progress?.Report(new AudioRecoveryProgress(0.60, "Padding",
                    $"Adding {edgeBytes:N0} bytes ({edgeBytes / (double)CddaBytesPerSecond:N3}s) digital silence at each edge..."));
                await BuildPaddedSearchFileAsync(combinedPath, searchPath, edgeBytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                searchPath = combinedPath;
            }

            IReadOnlyList<SearchResult> results;
            try
            {
                progress?.Report(new AudioRecoveryProgress(0.62, "FindCRCs",
                    $"Searching concatenated CDDA at {CddaBytesPerStereoFrame}-byte stereo-sample alignment..."));
                results = await _searchEngine.SearchAsync(searchPath, targets, CddaBytesPerStereoFrame, searchProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsSearchPath)
                {
                    try { if (File.Exists(searchPath)) File.Delete(searchPath); } catch { }
                }
            }

            // Normalize offsets back to the unpadded combined stream before any
            // edge-recovery logic. A valid first-track start may therefore be negative.
            IReadOnlyList<SearchResult> normalizedResults = results
                .Select(r => r.Found && r.Offset is long offset ? r with { Offset = offset - edgeBytes } : r)
                .ToArray();

            if (attemptUnderdumpedEdgeRepair || saveEdgePartials)
            {
                progress?.Report(new AudioRecoveryProgress(0.995, "Edge recovery",
                    "Trying under-dumped first/last tracks from adjacent matched anchors..."));
                EdgeRecoveryOutcome edge = await _edgeRecoveryService.RepairAsync(
                    combinedPath, targets, normalizedResults, outputRoot, attemptUnderdumpedEdgeRepair, saveEdgePartials, activity, cancellationToken, enableHeadsTails, headsTailsSourceFile).ConfigureAwait(false);
                normalizedResults = edge.Results;
            }

            long[] boundaries = sources.Select(s => s.CombinedStartOffset)
                .Concat(new[] { combinedBytes })
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var recovered = new List<AudioRecoveredTrack>(normalizedResults.Count);
            foreach (SearchResult result in normalizedResults)
            {
                if (!result.Found || result.Offset is not long relative)
                {
                    recovered.Add(new AudioRecoveredTrack(result.Target, false, result.OutputPath, null, result.Offset, 0, 0, null, result.Status));
                    continue;
                }

                long end = relative + result.Target.Size;
                long leading = Math.Max(0, Math.Min(result.Target.Size, -relative));
                long trailing = Math.Max(0, end - combinedBytes);
                long nearestBoundary = boundaries.OrderBy(b => Math.Abs(relative - b)).FirstOrDefault();
                long delta = relative - nearestBoundary;
                recovered.Add(new AudioRecoveredTrack(
                    result.Target,
                    true,
                    result.OutputPath,
                    relative + edgeBytes,
                    relative,
                    leading,
                    trailing,
                    delta,
                    result.Status));
            }

            progress?.Report(new AudioRecoveryProgress(1, "Complete",
                $"Recovered {recovered.Count(t => t.Found):N0}/{recovered.Count:N0} target tracks."));
            return new AudioRecoveryResult(outputRoot, combinedPath, combinedBytes, edgeBytes, sources, recovered);
        }, cancellationToken);
    }

    private static void AddPlaylistPath(List<string> paths, string playlistDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        value = value.Trim().Trim('"');
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile)
                return;
            path = uri.LocalPath;
        }
        else
        {
            path = Path.IsPathRooted(value) ? value : Path.Combine(playlistDirectory, value);
        }

        path = Path.GetFullPath(path);
        if (!IsSupportedAudioSourcePath(path))
            return;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Playlist references missing audio source: {path}", path);
        paths.Add(path);
    }

    private static async Task ConcatenateAsync(IEnumerable<string> files, string outputPath, CancellationToken cancellationToken)
    {
        string partial = outputPath + ".partial";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (string file in files)
                {
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(partial, outputPath, true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task BuildPaddedSearchFileAsync(string combinedPath, string searchPath, long padBytes, CancellationToken cancellationToken)
    {
        string partial = searchPath + ".partial";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        buffer.AsSpan().Clear();
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteZerosAsync(output, padBytes, buffer, cancellationToken).ConfigureAwait(false);
                await using (var input = new FileStream(combinedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                buffer.AsSpan().Clear();
                await WriteZerosAsync(output, padBytes, buffer, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(partial, searchPath, true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteZerosAsync(Stream output, long bytes, byte[] zeroBuffer, CancellationToken cancellationToken)
    {
        long remaining = bytes;
        while (remaining > 0)
        {
            int count = (int)Math.Min(zeroBuffer.Length, remaining);
            await output.WriteAsync(zeroBuffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "track" : value;
    }
}
