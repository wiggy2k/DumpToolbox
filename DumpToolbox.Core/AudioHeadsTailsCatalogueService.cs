using Microsoft.Data.Sqlite;
using SharpCompress.Archives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace DumpToolbox.Core;

public sealed record AudioHeadsTailsRoot(long Id, string Path, DateTimeOffset AddedUtc, DateTimeOffset? LastScannedUtc, DateTimeOffset? LastSuccessfulScanUtc, string? LastError);
public sealed record AudioHeadsTailsProgress(string CurrentPath, int SourcesProcessed, int SourcesTotal, int TracksExtracted, int SourcesSkipped, int SourcesErrored, int AllZeroTracks = 0);

public sealed class AudioHeadsTailsCorpusWriterSession : IAsyncDisposable
{
    private readonly FileStream _output;
    private readonly Channel<byte[]> _queue;
    private readonly Task _writerTask;
    private readonly IProgress<string>? _log;
    private long _bytesWritten;
    private long _bytesQueued;
    private int _completed;

    internal AudioHeadsTailsCorpusWriterSession(string corpusPath, IProgress<string>? log)
    {
        CorpusPath = Path.GetFullPath(corpusPath);
        Directory.CreateDirectory(Path.GetDirectoryName(CorpusPath)!);
        _log = log;
        _output = new FileStream(CorpusPath, FileMode.Append, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _writerTask = Task.Run(WriterLoopAsync);
        _bytesWritten = _output.Length;
        _log?.Report($"CORPUS: opened {CorpusPath} for append at offset {_output.Length:N0}; dedicated writer is ready for scan workers.");
    }

    public string CorpusPath { get; }
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    internal void Report(string message) => _log?.Report(message);

    public async ValueTask AppendSnippetsAsync(IEnumerable<(int track, byte[] head, byte[] tail)> snippets, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        int count = 0;
        foreach (var snippet in snippets)
        {
            ct.ThrowIfCancellationRequested();
            ms.Write(snippet.head);
            ms.Write(snippet.tail);
            count++;
        }
        if (count == 0) return;
        byte[] chunk = ms.ToArray();
        long queued = Interlocked.Add(ref _bytesQueued, chunk.Length);
        await _queue.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);
        _log?.Report($"CORPUS: queued {count:N0} track pair(s) / {chunk.Length:N0} byte(s); {queued:N0} byte(s) queued during this scan.");
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _queue.Writer.TryComplete();
        await _writerTask.WaitAsync(ct).ConfigureAwait(false);
        await _output.FlushAsync(ct).ConfigureAwait(false);
        _log?.Report($"CORPUS: writer complete; {BytesWritten:N0} byte(s) written to {CorpusPath}.");
    }

    private async Task WriterLoopAsync()
    {
        long nextLog = 1024 * 1024;
        try
        {
            await foreach (byte[] chunk in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _output.WriteAsync(chunk).ConfigureAwait(false);
                long total = Interlocked.Add(ref _bytesWritten, chunk.Length);
                if (total >= nextLog)
                {
                    _log?.Report($"CORPUS: {total:N0} byte(s) streamed so far.");
                    while (nextLog <= total) nextLog += 1024 * 1024;
                }
            }
        }
        catch (Exception ex)
        {
            _queue.Writer.TryComplete(ex);
            _log?.Report($"CORPUS ERROR: writer failed: {ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await CompleteAsync().ConfigureAwait(false); }
        finally { await _output.DisposeAsync().ConfigureAwait(false); }
    }
}

public sealed class AudioHeadsTailsCatalogueService
{
    private const int SnippetBytes = 256;
    private const int BufferBytes = 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int LegacySnippetBlobMigrationDone;
    private readonly CueSheetAnalysisService _cue = new();
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".7z", ".rar", ".arj", ".ace", ".arc", ".zst", ".gz", ".bz2", ".xz", ".lz", ".z", ".tar", ".tgz", ".tbz", ".tbz2", ".txz", ".tzst"
    };

    public string DatabasePath { get; } = Path.Combine(AppContext.BaseDirectory, "audio_heads_tails.sqlite");

    public async Task<IReadOnlyList<AudioHeadsTailsRoot>> GetRootsAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,path,added_utc,last_scanned_utc,last_success_utc,last_error FROM roots WHERE active=1 ORDER BY path COLLATE NOCASE";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<AudioHeadsTailsRoot>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new AudioHeadsTailsRoot(r.GetInt64(0), r.GetString(1), ParseDate(r.GetString(2)), ReadDate(r, 3), ReadDate(r, 4), r.IsDBNull(5) ? null : r.GetString(5)));
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<long> AddRootAsync(string path, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO roots(path,active,added_utc) VALUES($p,1,$n) ON CONFLICT(path) DO UPDATE SET active=1 RETURNING id";
            cmd.Parameters.AddWithValue("$p", full);
            cmd.Parameters.AddWithValue("$n", Now());
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        finally { Gate.Release(); }
    }

    public async Task DeactivateRootAsync(long id, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE roots SET active=0 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<AudioHeadsTailsCorpusWriterSession> BeginCorpusScanAsync(string corpusPath, IReadOnlyCollection<long> rootsBeingScanned, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(corpusPath)) throw new ArgumentException("Choose a Heads and Tails corpus output path.", nameof(corpusPath));
        string full = Path.GetFullPath(corpusPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await PrepareAppendOnlyCorpusAsync(full, log, ct).ConfigureAwait(false);
        return new AudioHeadsTailsCorpusWriterSession(full, log);
    }

    private async Task PrepareAppendOnlyCorpusAsync(string corpusPath, IProgress<string>? log, CancellationToken ct)
    {
        bool exists = File.Exists(corpusPath);
        long length = exists ? new FileInfo(corpusPath).Length : 0;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            string? previousPath = null;
            using (var c = db.CreateCommand())
            {
                c.CommandText = "SELECT value FROM meta WHERE key='corpus_path'";
                previousPath = await c.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }

            bool pathChanged = !string.IsNullOrWhiteSpace(previousPath) && !Path.GetFullPath(previousPath).Equals(corpusPath, StringComparison.OrdinalIgnoreCase);
            bool needsSourceReplay = !exists || pathChanged;

            // First migration from the older BLOB-backed catalogue: if a non-empty corpus already exists,
            // adopt it rather than duplicating every source. If it is missing/empty, force source reprocessing.
            if (string.IsNullOrWhiteSpace(previousPath) && exists && length > 0)
                needsSourceReplay = false;

            if (needsSourceReplay)
            {
                using var reset = db.CreateCommand();
                reset.CommandText = "UPDATE sources SET signature='' WHERE present=1";
                int resetCount = await reset.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                log?.Report($"CORPUS: {(pathChanged ? "configured path changed" : "configured file is missing")}; invalidated {resetCount:N0} processed source signature(s) so their snippets will be re-read from source and appended.");
            }

            using (var set = db.CreateCommand())
            {
                set.CommandText = "INSERT INTO meta(key,value) VALUES('corpus_path',$p) ON CONFLICT(key) DO UPDATE SET value=$p";
                set.Parameters.AddWithValue("$p", corpusPath);
                await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally { Gate.Release(); }
    }

    public async Task ScanRootAsync(long rootId, int maxConcurrency = 4, IProgress<AudioHeadsTailsProgress>? progress = null, IProgress<string>? log = null, CancellationToken ct = default, AudioHeadsTailsCorpusWriterSession? corpus = null)
    {
        maxConcurrency = Math.Clamp(maxConcurrency, 1, 64);
        AudioHeadsTailsRoot root = (await GetRootsAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == rootId)
            ?? throw new InvalidOperationException("Heads and Tails collection is no longer registered.");
        if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException(root.Path);

        log?.Report($"Collection: {root.Path}");
        log?.Report($"Enumeration started; recursively looking for loose CUEs and supported archives (worker limit {maxConcurrency}).");

        var looseCueList = new List<string>();
        var archiveList = new List<string>();
        long filesSeen = 0;
        var enumWatch = Stopwatch.StartNew();
        long lastEnumLogMs = 0;
        foreach (string path in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            filesSeen++;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)) looseCueList.Add(path);
            else if (IsArchive(path)) archiveList.Add(path);

            long ms = enumWatch.ElapsedMilliseconds;
            if (filesSeen == 1 || filesSeen % 5000 == 0 || ms - lastEnumLogMs >= 5000)
            {
                lastEnumLogMs = ms;
                log?.Report($"Enumeration: {filesSeen:N0} file(s) visited; {looseCueList.Count:N0} loose CUE(s); {archiveList.Count:N0} archive(s) found so far...");
            }
        }

        string[] looseCues = looseCueList.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] archives = archiveList.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        int total = looseCues.Length + archives.Length;
        log?.Report($"Enumeration complete in {enumWatch.Elapsed}: {filesSeen:N0} file(s) visited; {looseCues.Length:N0} loose CUE(s) and {archives.Length:N0} supported archive(s) queued.");
        if (total == 0)
            log?.Report("No loose CUEs or supported archives were found in this collection.");

        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int done = 0, tracks = 0, skipped = 0, errors = 0, allZero = 0;

        void ReportProgress(string current)
        {
            progress?.Report(new AudioHeadsTailsProgress(
                current,
                Volatile.Read(ref done),
                total,
                Volatile.Read(ref tracks),
                Volatile.Read(ref skipped),
                Volatile.Read(ref errors),
                Volatile.Read(ref allZero)));
        }

        var work = looseCues.Select(p => (Path: p, Archive: false))
            .Concat(archives.Select(p => (Path: p, Archive: true)))
            .ToArray();

        var options = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct };
        await Parallel.ForEachAsync(work, options, async (item, token) =>
        {
            if (!item.Archive)
            {
                string cuePath = item.Path;
                string rel = Norm(Path.GetRelativePath(root.Path, cuePath));
                seen.TryAdd(rel, 0);
                log?.Report($"START loose CUE: {rel}");
                try
                {
                    CueSheetAnalysis analysis = await _cue.AnalyzeAsync(cuePath, token).ConfigureAwait(false);
                    log?.Report($"CUE parsed: {rel}: {analysis.Tracks.Count:N0} track record(s), audio={(analysis.HasAudio ? "yes" : "no")}.");
                    if (!analysis.HasAudio)
                    {
                        string sig = BuildLooseSignature(cuePath, analysis);
                        await SaveSourceAsync(rootId, rel, sig, new(), new(), token).ConfigureAwait(false);
                        log?.Report($"SKIP loose CUE: {rel}: no AUDIO tracks; catalogue source cleared.");
                    }
                    else
                    {
                        string sig = BuildLooseSignature(cuePath, analysis);
                        if (await IsUnchangedAsync(rootId, rel, sig, token).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref skipped);
                            log?.Report($"UNCHANGED loose CUE: {rel}; already represented in the append-only corpus; no source read and no corpus write.");
                        }
                        else
                        {
                            log?.Report($"Extracting heads/tails from loose CUE: {rel}");
                            TrackSnippetResult extracted = ExtractLooseSnippets(cuePath, analysis, token);
                            if (corpus is not null) await corpus.AppendSnippetsAsync(extracted.Snippets, token).ConfigureAwait(false);
                            log?.Report($"Saving processed-source metadata: {rel}: {extracted.Snippets.Count:N0} captured, {extracted.AllZeroTracks.Count:N0} all-zero; no snippet bytes are stored in SQLite.");
                            await SaveSourceAsync(rootId, rel, sig, extracted.Snippets, extracted.AllZeroTracks, token).ConfigureAwait(false);
                            Interlocked.Add(ref tracks, extracted.Snippets.Count);
                            Interlocked.Add(ref allZero, extracted.AllZeroTracks.Count);
                            log?.Report($"DONE loose CUE: {rel}: {extracted.Snippets.Count:N0} audio track(s) captured, {extracted.AllZeroTracks.Count:N0} all-zero track(s).");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    log?.Report($"ERROR loose CUE {rel}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref done);
                    ReportProgress(rel);
                }
                return;
            }

            string archivePath = item.Path;
            string archiveRel = Norm(Path.GetRelativePath(root.Path, archivePath));
            string archiveSig = BuildArchiveSignature(archivePath);
            var archiveWatch = Stopwatch.StartNew();
            var archiveHandledSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            log?.Report($"START archive: {archiveRel} ({new FileInfo(archivePath).Length:N0} bytes)");
            try
            {
                using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
                token.ThrowIfCancellationRequested();
                var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).ToList();
                log?.Report($"OPEN archive: {archiveRel}: {entries.Count:N0} file entr{(entries.Count == 1 ? "y" : "ies")} indexed.");
                var byKey = entries
                    .GroupBy(e => Norm(e.Key!), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var cueEntries = entries.Where(e => Path.GetExtension(e.Key!).Equals(".cue", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

                log?.Report($"Archive {archiveRel}: {cueEntries.Count:N0} CUE(s) found; processing in-place without full extraction.");
                if (cueEntries.Count == 0)
                    log?.Report($"SKIP archive: {archiveRel}: no CUE entries.");

                foreach (var cueEntry in cueEntries)
                {
                    token.ThrowIfCancellationRequested();
                    string cueKey = Norm(cueEntry.Key!);
                    string sourceKey = archiveRel + "::" + cueKey;
                    seen.TryAdd(sourceKey, 0);
                    string sig = archiveSig + "|" + cueKey + "|" + cueEntry.Size;
                    try
                    {
                        log?.Report($"CUE in archive: {sourceKey}: checking catalogue state.");
                        if (await IsUnchangedAsync(rootId, sourceKey, sig, token).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref skipped);
                            archiveHandledSources.Add(sourceKey);
                            log?.Report($"UNCHANGED archive CUE: {sourceKey}; already represented in the append-only corpus; no source read and no corpus write.");
                            continue;
                        }

                        string cueText;
                        log?.Report($"Reading CUE entry: {sourceKey} ({cueEntry.Size:N0} bytes)");
                        using (Stream stream = cueEntry.OpenEntryStream())
                        using (var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferBytes, leaveOpen: false))
                            cueText = await sr.ReadToEndAsync(token).ConfigureAwait(false);

                        CueSheetAnalysis analysis = await _cue.AnalyzeTextAsync(cueText, sourceKey, token).ConfigureAwait(false);
                        int audioTracks = analysis.Tracks.Count(t => t.IsAudio);
                        log?.Report($"Parsed {sourceKey}: {analysis.Tracks.Count:N0} track record(s), {audioTracks:N0} AUDIO track(s).");
                        if (!analysis.HasAudio)
                        {
                            await SaveSourceAsync(rootId, sourceKey, sig, new(), new(), token).ConfigureAwait(false);
                            archiveHandledSources.Add(sourceKey);
                            log?.Report($"SKIP archive CUE: {sourceKey}: no AUDIO tracks; catalogue source cleared.");
                            continue;
                        }

                        TrackSnippetResult extracted = ExtractArchiveSnippets(archive, byKey, cueKey, analysis, log, sourceKey, token);
                        if (corpus is not null) await corpus.AppendSnippetsAsync(extracted.Snippets, token).ConfigureAwait(false);
                        log?.Report($"Saving processed-source metadata: {sourceKey}: {extracted.Snippets.Count:N0} captured, {extracted.AllZeroTracks.Count:N0} all-zero; no snippet bytes are stored in SQLite.");
                        await SaveSourceAsync(rootId, sourceKey, sig, extracted.Snippets, extracted.AllZeroTracks, token).ConfigureAwait(false);
                        archiveHandledSources.Add(sourceKey);
                        Interlocked.Add(ref tracks, extracted.Snippets.Count);
                        Interlocked.Add(ref allZero, extracted.AllZeroTracks.Count);
                        log?.Report($"DONE archive CUE: {sourceKey}: {extracted.Snippets.Count:N0} audio track(s) captured, {extracted.AllZeroTracks.Count:N0} all-zero track(s).");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        archiveHandledSources.Add(sourceKey);
                        log?.Report($"ERROR archive CUE {sourceKey}: {ex.Message}");
                    }
                }
                log?.Report($"DONE archive: {archiveRel} in {archiveWatch.Elapsed}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                log?.Report($"ERROR archive {archiveRel}: {ex.Message}");
            }
            finally
            {
                Interlocked.Increment(ref done);
                ReportProgress(archiveRel);
            }
        }).ConfigureAwait(false);

        var seenSet = new HashSet<string>(seen.Keys, StringComparer.OrdinalIgnoreCase);
        int errorCount = Volatile.Read(ref errors);
        log?.Report($"Finalizing collection catalogue: {seenSet.Count:N0} source key(s) seen this scan.");
        await FinalizeRootAsync(rootId, seenSet, errorCount == 0 ? null : $"{errorCount} source(s) had errors", errorCount == 0, ct).ConfigureAwait(false);
        log?.Report($"Collection complete: {Volatile.Read(ref tracks):N0} track head/tail pair(s), {Volatile.Read(ref allZero):N0} all-zero audio track(s), {Volatile.Read(ref skipped):N0} unchanged source(s), {errorCount:N0} error(s).");
    }

    public async Task<bool> HasActiveCollectionsAsync(CancellationToken ct = default) => (await GetRootsAsync(ct).ConfigureAwait(false)).Count > 0;

    private TrackSnippetResult ExtractLooseSnippets(string cuePath, CueSheetAnalysis a, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(cuePath)!;
        var snippets = new List<(int track, byte[] head, byte[] tail)>();
        var zeroTracks = new List<int>();
        for (int i = 0; i < a.Tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            CueTrackAnalysis t = a.Tracks[i];
            if (!t.IsAudio) continue;
            if (!t.FileType.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Track {t.Number:00} references {t.FileType}; Heads and Tails requires raw BINARY CUE payloads.");

            string file = Path.GetFullPath(Path.Combine(dir, t.FileName));
            if (!File.Exists(file)) throw new FileNotFoundException($"Referenced audio image not found: {t.FileName}", file);
            long length = new FileInfo(file).Length;
            (long start, long end) = GetTrackRange(a, i, t.FileName, length);
            if (end <= start) continue;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes, FileOptions.RandomAccess);
            byte[]? head = ReadHead(fs, start, end, ct);
            byte[]? tail = ReadTail(fs, start, end, ct);
            if (head is null || tail is null) zeroTracks.Add(t.Number);
            else snippets.Add((t.Number, head, tail));
        }
        return new(snippets, zeroTracks);
    }

    private TrackSnippetResult ExtractArchiveSnippets(IArchive archive, Dictionary<string, IArchiveEntry> byKey, string cueKey, CueSheetAnalysis a, IProgress<string>? log, string sourceKey, CancellationToken ct)
    {
        var snippets = new List<(int track, byte[] head, byte[] tail)>();
        var zeroTracks = new List<int>();
        string cueDir = ArchiveDirectory(cueKey);

        foreach (var fileGroup in a.Tracks.Where(t => t.IsAudio).GroupBy(t => t.FileName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            CueTrackAnalysis first = fileGroup.First();
            if (!first.FileType.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Track {first.Number:00} references {first.FileType}; Heads and Tails requires raw BINARY CUE payloads.");

            string entryKey = ResolveArchivePath(cueDir, first.FileName);
            if (!byKey.TryGetValue(entryKey, out IArchiveEntry? payload))
            {
                // Some CUEs use only a basename while the archive adds an outer directory.
                string basename = Path.GetFileName(entryKey.Replace('/', Path.DirectorySeparatorChar));
                payload = byKey.Values.FirstOrDefault(e => Path.GetFileName((e.Key ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)).Equals(basename, StringComparison.OrdinalIgnoreCase));
            }
            if (payload is null) throw new FileNotFoundException($"Archive payload referenced by CUE was not found: {first.FileName}");

            var ranges = new List<TrackRange>();
            foreach (CueTrackAnalysis t in fileGroup.OrderBy(t => t.Number))
            {
                int idx = a.Tracks.ToList().IndexOf(t);
                (long start, long end) = GetTrackRange(a, idx, t.FileName, payload.Size);
                if (end > start) ranges.Add(new TrackRange(t.Number, start, end));
            }

            log?.Report($"Payload: {sourceKey}: opening {entryKey} ({payload.Size:N0} bytes) for {ranges.Count:N0} AUDIO track range(s).");
            using Stream input = payload.OpenEntryStream();
            Dictionary<int, SnippetAccumulator> acc = ProcessSequentialRanges(input, ranges, log, sourceKey + "::" + entryKey, payload.Size, ct);
            log?.Report($"Payload: {sourceKey}: finished streaming {entryKey}.");
            foreach (TrackRange range in ranges)
            {
                SnippetAccumulator s = acc[range.Track];
                if (!s.HasNonZero) zeroTracks.Add(range.Track);
                else snippets.Add((range.Track, s.GetHead(), s.GetTail()));
            }
        }
        return new(snippets, zeroTracks);
    }

    private static Dictionary<int, SnippetAccumulator> ProcessSequentialRanges(Stream input, List<TrackRange> ranges, IProgress<string>? log, string label, long totalBytes, CancellationToken ct)
    {
        var result = ranges.ToDictionary(r => r.Track, _ => new SnippetAccumulator());
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        byte[] buffer = new byte[BufferBytes];
        long pos = 0;
        long nextLog = 256L * 1024 * 1024;
        int rangeIndex = 0;
        void MaybeLog()
        {
            if (pos < nextLog) return;
            double pct = totalBytes > 0 ? Math.Min(100.0, pos * 100.0 / totalBytes) : 0.0;
            log?.Report($"Streaming payload: {label}: {pos:N0}/{totalBytes:N0} bytes ({pct:F1}%).");
            while (nextLog <= pos) nextLog += 256L * 1024 * 1024;
        }
        while (rangeIndex < ranges.Count)
        {
            ct.ThrowIfCancellationRequested();
            TrackRange r = ranges[rangeIndex];
            if (pos < r.Start)
            {
                long need = r.Start - pos;
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, need));
                if (n <= 0) throw new EndOfStreamException($"Archive payload ended before track {r.Track:00}.");
                pos += n;
                MaybeLog();
                continue;
            }
            if (pos >= r.End) { rangeIndex++; continue; }
            int take = (int)Math.Min(buffer.Length, r.End - pos);
            int got = input.Read(buffer, 0, take);
            if (got <= 0) throw new EndOfStreamException($"Archive payload ended inside track {r.Track:00}.");
            result[r.Track].Feed(buffer.AsSpan(0, got));
            pos += got;
            MaybeLog();
            if (pos >= r.End) rangeIndex++;
        }
        return result;
    }

    private static (long start, long end) GetTrackRange(CueSheetAnalysis a, int i, string fileName, long fileLength)
    {
        CueTrackAnalysis t = a.Tracks[i];
        long start = (long)t.Index01Frames * 2352;
        long end = fileLength;
        if (i + 1 < a.Tracks.Count)
        {
            CueTrackAnalysis n = a.Tracks[i + 1];
            if (n.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                int boundary = n.Index00Frames is int i0 && i0 >= t.Index01Frames ? i0 : n.Index01Frames;
                end = Math.Min(fileLength, (long)boundary * 2352);
            }
        }
        return (Math.Max(0, start), Math.Max(0, end));
    }

    private static byte[]? ReadHead(FileStream fs, long start, long end, CancellationToken ct)
    {
        byte[] buf = new byte[64 * 1024]; long pos = start;
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested(); fs.Position = pos;
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, end - pos)); if (n <= 0) break;
            int k = Array.FindIndex(buf, 0, n, b => b != 0);
            if (k >= 0) { long at = pos + k; return ReadRange(fs, at, (int)Math.Min(SnippetBytes, end - at)); }
            pos += n;
        }
        return null;
    }

    private static byte[]? ReadTail(FileStream fs, long start, long end, CancellationToken ct)
    {
        byte[] buf = new byte[64 * 1024]; long pos = end;
        while (pos > start)
        {
            ct.ThrowIfCancellationRequested(); int n = (int)Math.Min(buf.Length, pos - start); long block = pos - n;
            fs.Position = block; int got = fs.Read(buf, 0, n);
            for (int k = got - 1; k >= 0; k--)
                if (buf[k] != 0) { long last = block + k; long at = Math.Max(start, last - SnippetBytes + 1); return ReadRange(fs, at, (int)(last - at + 1)); }
            pos = block;
        }
        return null;
    }

    private static byte[] ReadRange(FileStream fs, long at, int length)
    {
        byte[] b = new byte[length]; fs.Position = at; int o = 0;
        while (o < length) { int n = fs.Read(b, o, length - o); if (n <= 0) break; o += n; }
        if (o != length) Array.Resize(ref b, o);
        return b;
    }

    private string BuildLooseSignature(string cuePath, CueSheetAnalysis a)
    {
        var sb = new StringBuilder(); AppendSig(sb, cuePath); string dir = Path.GetDirectoryName(cuePath)!;
        foreach (string p in a.Tracks.Select(t => Path.GetFullPath(Path.Combine(dir, t.FileName))).Distinct(StringComparer.OrdinalIgnoreCase)) AppendSig(sb, p);
        return sb.ToString();
    }

    private static string BuildArchiveSignature(string archivePath)
    {
        FileInfo f = new(archivePath);
        return $"archive|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
    }

    private static void AppendSig(StringBuilder sb, string p)
    {
        var f = new FileInfo(p);
        sb.Append(Path.GetFileName(p)).Append('|').Append(f.Exists ? f.Length : -1).Append('|').Append(f.Exists ? f.LastWriteTimeUtc.Ticks : 0).Append(';');
    }

    private async Task<bool> IsUnchangedAsync(long root, string rel, string sig, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            using var c = db.CreateCommand();
            c.CommandText = "SELECT signature FROM sources WHERE root_id=$r AND relative_path=$p AND present=1";
            c.Parameters.AddWithValue("$r", root); c.Parameters.AddWithValue("$p", rel);
            return (await c.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string == sig;
        }
        finally { Gate.Release(); }
    }

    private async Task SaveSourceAsync(long root, string rel, string sig, List<(int track, byte[] head, byte[] tail)> snippets, List<int> allZeroTracks, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
            using (var c = db.CreateCommand())
            {
                c.Transaction = (SqliteTransaction)tx;
                c.CommandText = "INSERT INTO sources(root_id,relative_path,signature,present,last_scanned_utc) VALUES($r,$p,$s,1,$n) ON CONFLICT(root_id,relative_path) DO UPDATE SET signature=$s,present=1,last_scanned_utc=$n";
                c.Parameters.AddWithValue("$r", root); c.Parameters.AddWithValue("$p", rel); c.Parameters.AddWithValue("$s", sig); c.Parameters.AddWithValue("$n", Now());
                await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            long sid;
            using (var c = db.CreateCommand())
            {
                c.Transaction = (SqliteTransaction)tx; c.CommandText = "SELECT id FROM sources WHERE root_id=$r AND relative_path=$p";
                c.Parameters.AddWithValue("$r", root); c.Parameters.AddWithValue("$p", rel); sid = Convert.ToInt64(await c.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            using (var d = db.CreateCommand())
            {
                d.Transaction = (SqliteTransaction)tx; d.CommandText = "DELETE FROM track_observations WHERE source_id=$s"; d.Parameters.AddWithValue("$s", sid); await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var x in snippets)
            {
                using var o = db.CreateCommand(); o.Transaction = (SqliteTransaction)tx; o.CommandText = "INSERT INTO track_observations(source_id,track_number,is_all_zero) VALUES($s,$t,0)"; o.Parameters.AddWithValue("$s", sid); o.Parameters.AddWithValue("$t", x.track); await o.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (int track in allZeroTracks)
            {
                using var o = db.CreateCommand(); o.Transaction = (SqliteTransaction)tx; o.CommandText = "INSERT INTO track_observations(source_id,track_number,is_all_zero) VALUES($s,$t,1)"; o.Parameters.AddWithValue("$s", sid); o.Parameters.AddWithValue("$t", track); await o.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task FinalizeRootAsync(long root, HashSet<string> seen, string? error, bool markMissing, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            if (markMissing)
            {
                using var c = db.CreateCommand();
                c.CommandText = "SELECT id,relative_path FROM sources WHERE root_id=$r"; c.Parameters.AddWithValue("$r", root);
                await using var r = await c.ExecuteReaderAsync(ct).ConfigureAwait(false); var missing = new List<long>();
                while (await r.ReadAsync(ct).ConfigureAwait(false)) if (!seen.Contains(r.GetString(1))) missing.Add(r.GetInt64(0));
                await r.DisposeAsync().ConfigureAwait(false);
                foreach (long id in missing) { using var u = db.CreateCommand(); u.CommandText = "UPDATE sources SET present=0 WHERE id=$i"; u.Parameters.AddWithValue("$i", id); await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            }
            using (var c = db.CreateCommand())
            {
                c.CommandText = "UPDATE roots SET last_scanned_utc=$n,last_success_utc=CASE WHEN $e IS NULL THEN $n ELSE last_success_utc END,last_error=$e WHERE id=$r";
                c.Parameters.AddWithValue("$n", Now()); c.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value); c.Parameters.AddWithValue("$r", root);
                await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally { Gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection($"Data Source={DatabasePath}"); await db.OpenAsync(ct).ConfigureAwait(false);
        using (var c = db.CreateCommand()) { c.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        using (var c = db.CreateCommand())
        {
            c.CommandText = @"CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS roots(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE,active INTEGER NOT NULL DEFAULT 1,added_utc TEXT NOT NULL,last_scanned_utc TEXT,last_success_utc TEXT,last_error TEXT);
CREATE TABLE IF NOT EXISTS sources(id INTEGER PRIMARY KEY,root_id INTEGER NOT NULL,relative_path TEXT NOT NULL,signature TEXT NOT NULL,present INTEGER NOT NULL DEFAULT 1,last_scanned_utc TEXT NOT NULL,UNIQUE(root_id,relative_path));
CREATE TABLE IF NOT EXISTS track_observations(id INTEGER PRIMARY KEY,source_id INTEGER NOT NULL,track_number INTEGER NOT NULL,is_all_zero INTEGER NOT NULL DEFAULT 0,UNIQUE(source_id,track_number));
CREATE INDEX IF NOT EXISTS ix_sources_root ON sources(root_id,present);
CREATE INDEX IF NOT EXISTS ix_track_observations_source ON track_observations(source_id);";
            await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (Interlocked.CompareExchange(ref LegacySnippetBlobMigrationDone, 1, 0) == 0)
        {
            bool hadLegacyBlobTable;
            using (var check = db.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='snippets' LIMIT 1";
                hadLegacyBlobTable = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
            }
            if (hadLegacyBlobTable)
            {
                using (var drop = db.CreateCommand()) { drop.CommandText = "DROP TABLE snippets"; await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                using (var vacuum = db.CreateCommand()) { vacuum.CommandText = "VACUUM"; await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            }
        }
        return db;
    }

    private static bool IsArchive(string p) => ArchiveExtensions.Contains(Path.GetExtension(p));
    private static string ArchiveDirectory(string key) { string n = Norm(key); int slash = n.LastIndexOf('/'); return slash < 0 ? string.Empty : n[..slash]; }
    private static string ResolveArchivePath(string cueDir, string referenced)
    {
        string raw = Norm(referenced).TrimStart('/');
        string combined = string.IsNullOrEmpty(cueDir) ? raw : cueDir + "/" + raw;
        var stack = new List<string>();
        foreach (string part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }
    private static string Norm(string p) => p.Replace('\\', '/');
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static DateTimeOffset ParseDate(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ReadDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseDate(r.GetString(i));

    private sealed record TrackSnippetResult(List<(int track, byte[] head, byte[] tail)> Snippets, List<int> AllZeroTracks);
    private sealed record TrackRange(int Track, long Start, long End);

    private sealed class SnippetAccumulator
    {
        private readonly List<byte> _head = new(SnippetBytes);
        private readonly Queue<byte> _tail = new(SnippetBytes);
        private int _pendingZeros;
        public bool HasNonZero { get; private set; }

        public void Feed(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes)
            {
                if (!HasNonZero)
                {
                    if (b == 0) continue;
                    HasNonZero = true;
                    _head.Add(b);
                    AppendTail(b);
                    continue;
                }

                if (_head.Count < SnippetBytes) _head.Add(b);
                if (b == 0)
                {
                    _pendingZeros = Math.Min(SnippetBytes, _pendingZeros + 1);
                }
                else
                {
                    int zeros = Math.Min(SnippetBytes - 1, _pendingZeros);
                    for (int i = 0; i < zeros; i++) AppendTail(0);
                    _pendingZeros = 0;
                    AppendTail(b);
                }
            }
        }

        private void AppendTail(byte b)
        {
            if (_tail.Count == SnippetBytes) _tail.Dequeue();
            _tail.Enqueue(b);
        }

        public byte[] GetHead() => _head.ToArray();
        public byte[] GetTail() => _tail.ToArray();
    }
}
