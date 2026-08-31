using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Text;

namespace DumpToolbox.Core;

public sealed class SkeletoolCatalogueService
{
    private const int SchemaVersion = 4;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly SkeletonResurrectionService _skeleton = new();
    private readonly CueSheetAnalysisService _cue = new();

    public string DatabasePath { get; } = Path.Combine(AppContext.BaseDirectory, "skeletool_sha1_catalogue.sqlite");

    // Materialized archive images/files are working data, not catalogue data. Keep them
    // in a per-process temporary cache so the persistent SHA-1 catalogue never grows a
    // second copy of the user's collection beside the executable.
    private static readonly string LegacyCacheDirectory = Path.Combine(AppContext.BaseDirectory, "skeletool_sha1_cache");
    private static readonly string TempCacheRoot = Path.Combine(Path.GetTempPath(), "DumpToolbox", "skeletool_sha1_cache");
    private static readonly string SessionCacheDirectory = Path.Combine(TempCacheRoot, $"{Environment.ProcessId}_{Guid.NewGuid():N}");
    private static int _cacheInitialized;

    public string CacheDirectory
    {
        get
        {
            EnsureTemporaryCache();
            return SessionCacheDirectory;
        }
    }

    public SkeletoolCatalogueService() => EnsureTemporaryCache();

    public async Task<IReadOnlyList<SkeletoolCatalogueRoot>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,path,active,added_utc,last_scanned_utc,last_success_utc,last_error FROM roots WHERE active=1 ORDER BY path COLLATE NOCASE";
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<SkeletoolCatalogueRoot>();
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new SkeletoolCatalogueRoot(
                    r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0,
                    ParseDate(r.GetString(3)), ReadDate(r, 4), ReadDate(r, 5), r.IsDBNull(6) ? null : r.GetString(6)));
            }
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<long> AddRootAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO roots(path,active,added_utc) VALUES($path,1,$now)
ON CONFLICT(path) DO UPDATE SET active=1
RETURNING id;";
            cmd.Parameters.AddWithValue("$path", full);
            cmd.Parameters.AddWithValue("$now", Now());
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { Gate.Release(); }
    }

    public async Task DeactivateRootAsync(long rootId, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE roots SET active=0 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", rootId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task ScanRootAsync(long rootId, IProgress<SkeletoolCatalogueProgress>? progress = null, int workerCount = 1,
        IProgress<string>? activityLog = null, CancellationToken cancellationToken = default)
    {
        SkeletoolCatalogueRoot root = (await GetRootsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(x => x.Id == rootId)
            ?? throw new InvalidOperationException("The SHA-1 catalogue folder is no longer registered.");
        if (!Directory.Exists(root.Path))
        {
            await UpdateRootFailureAsync(rootId, $"Folder is unavailable: {root.Path}", cancellationToken).ConfigureAwait(false);
            throw new DirectoryNotFoundException($"Catalogue folder is unavailable. Existing records were retained and were not marked missing: {root.Path}");
        }

        // Enumeration must succeed before any existing source is marked absent. A disconnected
        // drive or permissions failure therefore cannot invalidate the historical catalogue.
        string[] allFiles;
        try
        {
            string dbFull = Path.GetFullPath(DatabasePath);
            string cacheFull = Path.GetFullPath(CacheDirectory);
            allFiles = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                .Where(p => !Path.GetFullPath(p).Equals(dbFull, StringComparison.OrdinalIgnoreCase) && !IsUnderPath(p, cacheFull))
                .ToArray();
        }
        catch (Exception ex)
        {
            await UpdateRootFailureAsync(rootId, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }

        activityLog?.Report($"Scanning collection folder: {root.Path}");
        IReadOnlyList<DirectUnitPlan> directPlans = await BuildDirectPlansAsync(root.Path, allFiles, activityLog, cancellationToken).ConfigureAwait(false);
        string[] archives = allFiles.Where(p => IsArchive(p)).ToArray();
        var work = new List<CatalogueWorkItem>(directPlans.Count + archives.Length);
        work.AddRange(directPlans.Select(p => new CatalogueWorkItem(false, p.SourcePath, p)));
        work.AddRange(archives.Select(a => new CatalogueWorkItem(true, a, null)));

        int total = work.Count;
        int done = 0, imageCount = 0, fileCount = 0, skipped = 0, errors = 0;
        var seenUnitIds = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();
        int parallelism = Math.Clamp(workerCount, 1, 64);

        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        }, async (item, ct) =>
        {
            progress?.Report(new SkeletoolCatalogueProgress(item.IsArchive ? "Checking archive" : "Checking image", item.Path,
                Volatile.Read(ref done), total, Volatile.Read(ref imageCount), Volatile.Read(ref fileCount), Volatile.Read(ref skipped), 0, Volatile.Read(ref errors)));

            activityLog?.Report($"{(item.IsArchive ? "Checking archive" : "Checking image")}: {item.Path}");
            try
            {
                UnitScanResult result = item.IsArchive
                    ? await ScanArchiveUnitAsync(rootId, root.Path, item.Path, activityLog, ct).ConfigureAwait(false)
                    : await ScanDirectUnitAsync(rootId, root.Path, item.DirectPlan!, activityLog, ct).ConfigureAwait(false);

                seenUnitIds.TryAdd(result.UnitId, 0);
                Interlocked.Add(ref imageCount, result.ImagesScanned);
                Interlocked.Add(ref fileCount, result.FilesHashed);
                if (result.Skipped) Interlocked.Increment(ref skipped);
                if (result.Errors > 0) Interlocked.Add(ref errors, result.Errors);
                activityLog?.Report(result.Skipped
                    ? $"Unchanged: {item.Path}"
                    : result.Errors > 0
                        ? $"Completed with {result.Errors} error(s): {item.Path}"
                        : $"Scanned: {item.Path} ({result.ImagesScanned:N0} image(s), {result.FilesHashed:N0} filesystem file(s))");
                int finished = Interlocked.Increment(ref done);
                progress?.Report(new SkeletoolCatalogueProgress(result.Skipped
                        ? (item.IsArchive ? "Unchanged archive" : "Unchanged")
                        : result.Errors > 0 ? "Completed with errors"
                        : (item.IsArchive ? "Scanned archive" : "Scanned image"),
                    item.Path, finished, total, Volatile.Read(ref imageCount), Volatile.Read(ref fileCount), Volatile.Read(ref skipped), 0, Volatile.Read(ref errors)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                int errorCount = Interlocked.Increment(ref errors);
                activityLog?.Report($"ERROR: {item.Path}: {ex.GetType().Name}: {ex.Message}");

                // If this source was already catalogued, preserve its current record. A
                // read/parse failure is not evidence that the source has disappeared.
                string rel = Norm(Path.GetRelativePath(root.Path, item.Path));
                foreach (long id in await GetPresentUnitIdsForLocationAsync(rootId, rel, ct).ConfigureAwait(false))
                    seenUnitIds.TryAdd(id, 0);

                int finished = Interlocked.Increment(ref done);
                progress?.Report(new SkeletoolCatalogueProgress("Error - continuing", item.Path, finished, total,
                    Volatile.Read(ref imageCount), Volatile.Read(ref fileCount), Volatile.Read(ref skipped), 0, errorCount));
            }
        }).ConfigureAwait(false);

        int missing = await MarkMissingAfterSuccessfulEnumerationAsync(rootId, seenUnitIds.Keys.ToHashSet(), cancellationToken).ConfigureAwait(false);
        if (errors == 0)
            await UpdateRootSuccessAsync(rootId, cancellationToken).ConfigureAwait(false);
        else
            await UpdateRootCompletedWithErrorsAsync(rootId, $"Completed with {errors} source error(s); see scan log.", cancellationToken).ConfigureAwait(false);
        activityLog?.Report(errors == 0
            ? $"Scan complete: {root.Path}"
            : $"Scan complete with {errors} error(s): {root.Path}");
        progress?.Report(new SkeletoolCatalogueProgress(errors == 0 ? "Complete" : "Complete with errors", root.Path,
            done, total, imageCount, fileCount, skipped, missing, errors));
    }

    public async Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> FindMatchesAsync(
        SkeletonInspectionResult inspection,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (SkeletonContentEntry entry in inspection.Entries.Where(e => e.CanRestore && !e.IsEmpty))
            {
                foreach ((string? hash, bool xa) in new[] { (entry.Sha1, false), (entry.XaSha1, true) })
                {
                    if (xa || !IsSha1(hash)) continue; // catalogue hashes logical filesystem bytes, not XA Form2 payloads.
                    using SqliteCommand cmd = db.CreateCommand();
                    cmd.CommandText = @"
SELECT h.size,f.relative_path,f.image_lba,f.image_extents,
       i.id,i.entry_path,i.source_offset,i.source_length,i.scanner_kind,
       u.id,u.kind,u.current_path,u.sha1,u.last_seen_utc
FROM hashes h
JOIN files f ON f.hash_id=h.id
JOIN images i ON i.id=f.image_id
JOIN units u ON u.id=i.unit_id
JOIN roots r ON r.id=u.root_id
WHERE h.sha1=$sha1 AND u.present=1 AND u.last_scanned_utc IS NOT NULL AND r.active=1
ORDER BY CASE u.kind WHEN 'direct' THEN 0 ELSE 1 END, u.last_seen_utc DESC;";
                    cmd.Parameters.Add("$sha1", SqliteType.Blob).Value = Sha1Bytes(hash!);
                    await using SqliteDataReader rd = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        long size = rd.GetInt64(0);
                        SkeletonContentEntry resolved = ResolveEntryGeometry(entry, size);
                        if (resolved.DataLength != size) continue;
                        string unitKind = rd.GetString(10);
                        string sourcePath = rd.GetString(11);
                        if (!File.Exists(sourcePath)) continue;
                        long imageId = rd.GetInt64(4);
                        string imageEntry = rd.GetString(5);
                        long sourceOffset = rd.GetInt64(6);
                        long sourceLength = rd.GetInt64(7);
                        string scannerKind = rd.GetString(8);
                        string unitSha1 = Sha1Hex(rd.GetFieldValue<byte[]>(12));
                        long? imageLba = rd.IsDBNull(2) ? null : rd.GetInt64(2);
                        IReadOnlyList<SkeletonSourceImageExtent>? imageExtents = rd.IsDBNull(3)
                            ? null
                            : ParseImageExtents(rd.GetString(3));

                        string relativePath = rd.GetString(1);
                        var catalogueSource = new SkeletoolCatalogueMatchSource(
                            unitKind, sourcePath, unitSha1, imageId, imageEntry, sourceOffset, sourceLength, scannerKind, relativePath);

                        // Lookup must stay metadata-only.  Do not extract archive members or build
                        // temporary image/file payloads here: explicit SkeleTool sources may still
                        // replace this catalogue candidate.  Materialization is deferred until the
                        // resurrection button is pressed and only for catalogue matches that remain.
                        if (imageLba is long lba)
                        {
                            result[entry.Path] = new SkeletonSourceMatch(resolved, sourcePath, hash!.ToLowerInvariant(), false,
                                "SHA-1 catalogue image (deferred)", relativePath, lba, size,
                                SourceImageExtents: imageExtents, CatalogueSource: catalogueSource);
                        }
                        else if (scannerKind.Equals("7z", StringComparison.OrdinalIgnoreCase))
                        {
                            result[entry.Path] = new SkeletonSourceMatch(resolved, sourcePath, hash!.ToLowerInvariant(), false,
                                "SHA-1 catalogue extracted file (deferred)", relativePath, SourceLength: size,
                                CatalogueSource: catalogueSource);
                        }
                        if (result.ContainsKey(entry.Path)) break;
                    }
                    if (result.ContainsKey(entry.Path)) break;
                }
            }
        }
        finally { Gate.Release(); }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MaterializeMatchesForResurrectionAsync(
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        IProgress<string>? activityLog = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SkeletonSourceMatch>(matches, StringComparer.OrdinalIgnoreCase);
        var imagePaths = new Dictionary<(string UnitSha1, long ImageId), string>();

        foreach ((string path, SkeletonSourceMatch match) in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkeletoolCatalogueMatchSource? source = match.CatalogueSource;
            if (source is null)
                continue;

            // If this entry survived local-folder/image matching, the catalogue is now the
            // selected source for this particular file.  Only now are archive bytes touched.
            var imageKey = (source.UnitSha1, source.ImageId);
            if (!imagePaths.TryGetValue(imageKey, out string? imagePath))
            {
                activityLog?.Report($"SHA-1 catalogue: materializing selected image for '{path}'...");
                imagePath = await MaterializeImageAsync(
                    source.UnitKind, source.SourcePath, source.UnitSha1, source.ImageId,
                    source.ImageEntryPath, source.SourceOffset, source.SourceLength, cancellationToken).ConfigureAwait(false);
                imagePaths[imageKey] = imagePath;
            }

            if (match.SourceImageLba is not null)
            {
                result[path] = match with
                {
                    SourcePath = imagePath,
                    MatchMethod = "SHA-1 catalogue image",
                    CatalogueSource = null
                };
                continue;
            }

            if (source.ScannerKind.Equals("7z", StringComparison.OrdinalIgnoreCase))
            {
                string filePath = await MaterializeFileFromImageAsync(
                    imagePath, source.RelativePath, source.UnitSha1, source.ImageId, cancellationToken).ConfigureAwait(false);
                long expected = match.SourceLength ?? match.Entry.DataLength;
                if (!File.Exists(filePath) || new FileInfo(filePath).Length != expected)
                    throw new InvalidDataException($"SHA-1 catalogue payload '{source.RelativePath}' did not materialize at the expected {expected:N0} bytes.");

                result[path] = match with
                {
                    SourcePath = filePath,
                    MatchMethod = "SHA-1 catalogue extracted file",
                    CatalogueSource = null
                };
                continue;
            }

            throw new InvalidOperationException(
                $"SHA-1 catalogue match '{path}' cannot be materialized from scanner kind '{source.ScannerKind}'.");
        }

        return result;
    }

    public async Task<IReadOnlyList<SkeletoolEvidenceUnit>> GetPendingEvidenceUnitsAsync(int evidenceSchema, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT id,kind,current_path,relative_path,sha1 FROM units WHERE present=1 AND last_scanned_utc IS NOT NULL AND (evidence_gathered=0 OR COALESCE(evidence_schema,0)<$schema) ORDER BY current_path COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$schema", evidenceSchema);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<SkeletoolEvidenceUnit>();
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new SkeletoolEvidenceUnit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Sha1Hex(r.GetFieldValue<byte[]>(4))));
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<SkeletoolEvidenceImage>> GetEvidenceImagesAsync(long unitId, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT i.id,i.entry_path,i.display_name,i.source_offset,i.source_length,i.image_kind,i.scanner_kind,u.kind,u.current_path,u.sha1 FROM images i JOIN units u ON u.id=i.unit_id WHERE i.unit_id=$id ORDER BY i.id";
            cmd.Parameters.AddWithValue("$id", unitId);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<SkeletoolEvidenceImage>();
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new SkeletoolEvidenceImage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.IsDBNull(5)?null:r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), Sha1Hex(r.GetFieldValue<byte[]>(9))));
            return result;
        }
        finally { Gate.Release(); }
    }

    public Task<string> MaterializeEvidenceImageAsync(SkeletoolEvidenceImage image, CancellationToken cancellationToken = default)
        => MaterializeImageAsync(image.UnitKind, image.SourcePath, image.UnitSha1, image.Id, image.EntryPath, image.SourceOffset, image.SourceLength, cancellationToken);

    public async Task MarkEvidenceGatheredAsync(long unitId, int evidenceSchema, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE units SET evidence_gathered=1,evidence_gathered_utc=$n,evidence_schema=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$n", Now()); cmd.Parameters.AddWithValue("$s", evidenceSchema); cmd.Parameters.AddWithValue("$id", unitId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task ResetEvidenceGatheredAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE units SET evidence_gathered=0,evidence_gathered_utc=NULL,evidence_schema=NULL WHERE present=1";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<UnitScanResult> ScanDirectUnitAsync(long rootId, string rootPath, DirectUnitPlan plan, IProgress<string>? activityLog, CancellationToken ct)
    {
        FileInfo fi = new(plan.SourcePath);
        string rel = Norm(Path.GetRelativePath(rootPath, plan.SourcePath));
        string layout = plan.LayoutHash;
        ExistingUnit? unchanged = await FindUnchangedUnitAsync(rootId, rel, "direct", fi.Length, fi.LastWriteTimeUtc.Ticks, layout, ct).ConfigureAwait(false);
        if (unchanged is not null)
        {
            await TouchUnitAsync(unchanged.Id, rootId, plan.SourcePath, rel, ct).ConfigureAwait(false);
            return new UnitScanResult(unchanged.Id, true, 0, 0, 0);
        }

        string sha1 = await HashFileAsync(plan.SourcePath, ct).ConfigureAwait(false);
        ExistingUnit? moved = await FindUnitByIdentityAsync("direct", sha1, layout, ct).ConfigureAwait(false);
        if (moved is not null && await UnitHasFilesAsync(moved.Id, ct).ConfigureAwait(false))
        {
            await MarkSameLocationOlderUnitsMissingAsync(rootId, rel, moved.Id, ct).ConfigureAwait(false);
            await TouchUnitAsync(moved.Id, rootId, plan.SourcePath, rel, ct, fi.Length, fi.LastWriteTimeUtc.Ticks).ConfigureAwait(false);
            return new UnitScanResult(moved.Id, true, 0, 0, 0);
        }

        await MarkSameLocationOlderUnitsMissingAsync(rootId, rel, -1, ct).ConfigureAwait(false);
        long unitId = await InsertUnitAsync(rootId, "direct", plan.SourcePath, rel, fi.Length, fi.LastWriteTimeUtc.Ticks, sha1, layout, ct).ConfigureAwait(false);
        int images = 0, files = 0, errors = 0;
        foreach (ImagePlan imagePlan in plan.Images)
        {
            try
            {
                ScanOneImageResult scanned = await ScanOneImageAsync(unitId, imagePlan, ct).ConfigureAwait(false);
                images++; files += scanned.Files;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors++;
                activityLog?.Report($"ERROR: {imagePlan.DisplayName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (errors == 0)
            await SetUnitScannedAsync(unitId, ct).ConfigureAwait(false);
        return new UnitScanResult(unitId, false, images, files, errors);
    }

    private async Task<UnitScanResult> ScanArchiveUnitAsync(long rootId, string rootPath, string archive, IProgress<string>? activityLog, CancellationToken ct)
    {
        FileInfo fi = new(archive);
        string rel = Norm(Path.GetRelativePath(rootPath, archive));
        ExistingUnit? unchanged = await FindUnchangedUnitAsync(rootId, rel, "archive", fi.Length, fi.LastWriteTimeUtc.Ticks, string.Empty, ct).ConfigureAwait(false);
        if (unchanged is not null)
        {
            await TouchUnitAsync(unchanged.Id, rootId, archive, rel, ct).ConfigureAwait(false);
            return new UnitScanResult(unchanged.Id, true, 0, 0, 0);
        }

        string archiveSha1 = await HashFileAsync(archive, ct).ConfigureAwait(false);
        ExistingUnit? moved = await FindUnitByIdentityAsync("archive", archiveSha1, string.Empty, ct).ConfigureAwait(false);
        if (moved is not null && await UnitHasFilesAsync(moved.Id, ct).ConfigureAwait(false))
        {
            await MarkSameLocationOlderUnitsMissingAsync(rootId, rel, moved.Id, ct).ConfigureAwait(false);
            await TouchUnitAsync(moved.Id, rootId, archive, rel, ct, fi.Length, fi.LastWriteTimeUtc.Ticks).ConfigureAwait(false);
            return new UnitScanResult(moved.Id, true, 0, 0, 0);
        }

        string temp = MakeTempDirectory("archive_scan");
        try
        {
            await ExtractArchiveAsync(archive, temp, ct).ConfigureAwait(false);
            string[] extracted = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).ToArray();
            IReadOnlyList<DirectUnitPlan> imageUnits = await BuildDirectPlansAsync(temp, extracted, activityLog, ct).ConfigureAwait(false);
            await MarkSameLocationOlderUnitsMissingAsync(rootId, rel, -1, ct).ConfigureAwait(false);
            long unitId = await InsertUnitAsync(rootId, "archive", archive, rel, fi.Length, fi.LastWriteTimeUtc.Ticks, archiveSha1, string.Empty, ct).ConfigureAwait(false);
            int images = 0, files = 0, errors = 0;
            foreach (DirectUnitPlan extractedUnit in imageUnits)
            {
                try
                {
                    foreach (ImagePlan p in extractedUnit.Images)
                    {
                        string entryPath = Norm(Path.GetRelativePath(temp, p.SourcePath));
                        ImagePlan archivePlan = p with { SourceEntryPath = entryPath };
                        try
                        {
                            ScanOneImageResult scanned = await ScanOneImageAsync(unitId, archivePlan, ct).ConfigureAwait(false);
                            images++; files += scanned.Files;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            errors++;
                            activityLog?.Report($"ERROR: {archive} :: {entryPath}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    // The SHA-1 catalogue stores hashes/geometry only. Once every data
                    // track belonging to this extracted source image has been scanned, the
                    // materialized BIN/ISO is no longer useful. Delete it immediately rather
                    // than retaining large disc images until the whole archive/session ends.
                    // Multi-track CUEs are safe because all plans for one source file live in
                    // the same DirectUnitPlan and therefore complete before this cleanup.
                    TryDeleteFile(extractedUnit.SourcePath);
                }
            }
            if (errors == 0)
                await SetUnitScannedAsync(unitId, ct).ConfigureAwait(false);
            return new UnitScanResult(unitId, false, images, files, errors);
        }
        finally { TryDeleteDirectory(temp); }
    }

    private async Task<ScanOneImageResult> ScanOneImageAsync(long unitId, ImagePlan plan, CancellationToken ct)
    {
        string scanPath = plan.SourcePath;
        string? temporarySlice = null;
        if (plan.SourceOffset != 0 || plan.SourceLength != new FileInfo(plan.SourcePath).Length)
        {
            temporarySlice = Path.Combine(MakeTempDirectory("track"), Path.GetFileName(plan.SourcePath));
            await CopyRangeAsync(plan.SourcePath, temporarySlice, plan.SourceOffset, plan.SourceLength, ct).ConfigureAwait(false);
            scanPath = temporarySlice;
        }

        try
        {
            SkeletoolCatalogueImageContent content;
            try
            {
                content = await _skeleton.ScanImageContentsForCatalogueAsync(scanPath, ct).ConfigureAwait(false);
            }
            catch (Exception primary) when (primary is InvalidOperationException or EndOfStreamException)
            {
                try
                {
                    content = await ScanImageVia7ZipAsync(scanPath, ct).ConfigureAwait(false);
                }
                catch (Exception secondary) when (secondary is InvalidOperationException or IOException)
                {
                    // A .bin without a CUE can legitimately be audio-only or otherwise not
                    // contain a filesystem. It is not a catalogue scan failure. CUE-backed
                    // audio tracks are excluded before reaching this point.
                    return new ScanOneImageResult(0);
                }
            }
            string imageSha1 = await HashFileAsync(scanPath, ct).ConfigureAwait(false);
            await InsertImageAndFilesAsync(unitId, plan.SourceEntryPath, plan.DisplayName, plan.SourceOffset, plan.SourceLength,
                imageSha1, content, ct).ConfigureAwait(false);
            return new ScanOneImageResult(content.Files.Count);
        }
        finally
        {
            if (temporarySlice is not null) TryDeleteDirectory(Path.GetDirectoryName(temporarySlice)!);
        }
    }

    private async Task<SkeletoolCatalogueImageContent> ScanImageVia7ZipAsync(string imagePath, CancellationToken ct)
    {
        string temp = MakeTempDirectory("image_fs");
        try
        {
            await ExtractArchiveAsync(imagePath, temp, ct, allowZipFallback: false).ConfigureAwait(false);
            var files = new List<SkeletoolCatalogueImageFile>();
            foreach (string path in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(path);
                string sha1 = await HashFileAsync(path, ct).ConfigureAwait(false);
                files.Add(new SkeletoolCatalogueImageFile("/" + Norm(Path.GetRelativePath(temp, path)), fi.Length, sha1, null));
            }
            return new SkeletoolCatalogueImageContent(Path.GetFileNameWithoutExtension(imagePath), null, files, "7z");
        }
        finally { TryDeleteDirectory(temp); }
    }

    private async Task<IReadOnlyList<DirectUnitPlan>> BuildDirectPlansAsync(string root, IReadOnlyList<string> allFiles, IProgress<string>? activityLog, CancellationToken ct)
    {
        // A CUE is authoritative for every BIN it references. Referenced AUDIO BINs are
        // never probed as filesystems, and referenced mixed/data BINs are scanned only
        // through the data-track extents described by the CUE.
        var cueControlledBins = new HashSet<string>(PathComparer());
        var plans = new Dictionary<string, List<ImagePlan>>(PathComparer());
        var cueHashes = new Dictionary<string, List<string>>(PathComparer());

        foreach (string cuePath in allFiles.Where(p => Path.GetExtension(p).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
        {
            string cueDir = Path.GetDirectoryName(cuePath)!;
            string[] cueLines;
            try { cueLines = await File.ReadAllLinesAsync(cuePath, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { activityLog?.Report($"ERROR: {cuePath}: could not read CUE: {ex.Message}"); continue; }

            // Even if the higher-level CUE analyser rejects an unusual sheet, still mark
            // every referenced BIN as CUE-controlled so it cannot fall through and be
            // mistaken for a standalone data image.
            foreach (string referenced in ExtractCueReferencedFiles(cueLines, cueDir))
            {
                if (Path.GetExtension(referenced).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    cueControlledBins.Add(referenced);
            }

            CueSheetAnalysis analysis;
            try { analysis = await _cue.AnalyzeAsync(cuePath, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { activityLog?.Report($"ERROR: {cuePath}: could not analyse CUE: {ex.Message}"); continue; }

            string cueSha = await HashFileAsync(cuePath, ct).ConfigureAwait(false);
            for (int i = 0; i < analysis.Tracks.Count; i++)
            {
                CueTrackAnalysis t = analysis.Tracks[i];
                if (string.IsNullOrWhiteSpace(t.FileName)) continue;
                string source = Path.GetFullPath(Path.Combine(cueDir, t.FileName.Replace('/', Path.DirectorySeparatorChar)));
                if (Path.GetExtension(source).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    cueControlledBins.Add(source);
                if (t.IsAudio || !File.Exists(source)) continue;

                int sector = CueSectorSize(t.Type);
                if (sector is not (2048 or 2336 or 2352)) continue;
                long sourceLength = new FileInfo(source).Length;
                long start = checked((long)t.Index01Frames * sector);
                long end = sourceLength;
                CueTrackAnalysis? nextSame = analysis.Tracks.Skip(i + 1).FirstOrDefault(x =>
                    x.FileName.Equals(t.FileName, StringComparison.OrdinalIgnoreCase));
                if (nextSame is not null)
                    end = Math.Min(end, checked((long)nextSame.Index01Frames * sector));
                if (start < 0 || end <= start || end > sourceLength) continue;

                if (!plans.TryGetValue(source, out List<ImagePlan>? list)) plans[source] = list = new();
                list.Add(new ImagePlan(source, Norm(Path.GetRelativePath(root, source)),
                    $"{Path.GetFileName(source)} track {t.Number:00}", start, end - start));
                if (!cueHashes.TryGetValue(source, out List<string>? hs)) cueHashes[source] = hs = new();
                hs.Add($"{cueSha}:{t.Number}:{t.Type}:{start}:{end}");
            }
        }

        foreach (string path in allFiles.Where(IsDirectImage))
        {
            // Any BIN named by a CUE is governed exclusively by that CUE. This includes
            // audio-only BINs and unusual/malformed CUE layouts which we deliberately do
            // not reinterpret as standalone images.
            if (Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) && cueControlledBins.Contains(Path.GetFullPath(path)))
                continue;
            if (!plans.ContainsKey(path))
                plans[path] = new List<ImagePlan> { new(path, Norm(Path.GetRelativePath(root, path)), Path.GetFileName(path), 0, new FileInfo(path).Length) };
        }

        return plans.Select(kvp => new DirectUnitPlan(
            kvp.Key,
            kvp.Value,
            cueHashes.TryGetValue(kvp.Key, out List<string>? hs) ? HashText(string.Join("|", hs.OrderBy(x => x))) : string.Empty)).ToArray();
    }

    private static IEnumerable<string> ExtractCueReferencedFiles(IEnumerable<string> lines, string cueDir)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;
            string name;
            int firstQuote = line.IndexOf('"');
            if (firstQuote >= 0)
            {
                int secondQuote = line.IndexOf('"', firstQuote + 1);
                if (secondQuote <= firstQuote) continue;
                name = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
            else
            {
                string rest = line[5..].Trim();
                int space = rest.LastIndexOf(' ');
                name = space > 0 ? rest[..space].Trim() : rest;
            }
            if (string.IsNullOrWhiteSpace(name)) continue;
            string candidate = Path.GetFullPath(Path.Combine(cueDir, name.Replace('/', Path.DirectorySeparatorChar)));
            yield return candidate;
        }
    }

    private async Task<string> MaterializeImageAsync(string unitKind, string sourcePath, string unitSha1, long imageId, string entryPath, long sourceOffset, long sourceLength, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        string dir = Path.Combine(CacheDirectory, unitSha1[..Math.Min(16, unitSha1.Length)], imageId.ToString());
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, SafeName(Path.GetFileName(entryPath)));
        if (File.Exists(dest) && new FileInfo(dest).Length == sourceLength) return dest;

        string baseImage;
        if (unitKind.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceOffset == 0 && sourceLength == new FileInfo(sourcePath).Length) return sourcePath;
            baseImage = sourcePath;
        }
        else
        {
            string extracted = Path.Combine(dir, "archive_" + SafeName(Path.GetFileName(entryPath)));
            if (!File.Exists(extracted)) await ExtractSingleArchiveEntryAsync(sourcePath, entryPath, extracted, ct).ConfigureAwait(false);
            baseImage = extracted;
        }

        await CopyRangeAsync(baseImage, dest, sourceOffset, sourceLength, ct).ConfigureAwait(false);
        return dest;
    }

    private async Task<string> MaterializeFileFromImageAsync(string imagePath, string relativePath, string unitSha1, long imageId, CancellationToken ct)
    {
        string dir = Path.Combine(CacheDirectory, unitSha1[..Math.Min(16, unitSha1.Length)], imageId.ToString(), "files");
        string rel = relativePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        string dest = Path.Combine(dir, rel);
        if (File.Exists(dest)) return dest;

        string marker = Path.Combine(dir, ".extraction_complete");
        if (!File.Exists(marker))
        {
            if (Directory.Exists(dir)) TryDeleteDirectory(dir);
            Directory.CreateDirectory(dir);
            await ExtractArchiveAsync(imagePath, dir, ct, allowZipFallback: false).ConfigureAwait(false);
            await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        }

        if (File.Exists(dest)) return dest;
        string normalized = Norm(rel);
        string? discovered = Directory.EnumerateFiles(dir, Path.GetFileName(rel), SearchOption.AllDirectories)
            .FirstOrDefault(p => Norm(Path.GetRelativePath(dir, p)).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return discovered ?? throw new FileNotFoundException($"The catalogue file '{relativePath}' could not be extracted from '{imagePath}'.");
    }

    private async Task ExtractSingleArchiveEntryAsync(string archive, string entryPath, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var info = ArchiveFactory.GetArchiveInformation(archive);
        if (info?.SupportsRandomAccess == true)
        {
            using IArchive opened = ArchiveFactory.OpenArchive(archive);
            var match = opened.Entries.FirstOrDefault(item =>
                !item.IsDirectory && item.Key is string key &&
                Norm(key).Equals(Norm(entryPath), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new FileNotFoundException($"Archive entry '{entryPath}' was not found in '{archive}'.");

            await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using Stream input = match.OpenEntryStream();
            await input.CopyToAsync(output, 1024 * 1024, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        await using FileStream archiveStream = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IReader reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            string? entryKey = reader.Entry.Key;
            if (reader.Entry.IsDirectory || entryKey is null ||
                !Norm(entryKey).Equals(Norm(entryPath), StringComparison.OrdinalIgnoreCase))
                continue;

            await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using Stream input = reader.OpenEntryStream();
            await input.CopyToAsync(output, 1024 * 1024, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        throw new FileNotFoundException($"Archive entry '{entryPath}' was not found in '{archive}'.");
    }

    private Task ExtractArchiveAsync(string archive, string destination, CancellationToken ct, bool allowZipFallback = true)
    {
        // Archive handling is intentionally pure managed code. SharpCompress is bundled
        // into the application's .NET single-file publish, so no native 7z DLL or external
        // 7-Zip installation is required on Windows or Linux.
        _ = allowZipFallback; // retained to avoid changing the catalogue call surface.
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);

        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            CheckCrc = true
        };

        var info = ArchiveFactory.GetArchiveInformation(archive);
        if (info?.SupportsRandomAccess == true)
        {
            using IArchive opened = ArchiveFactory.OpenArchive(archive);
            opened.WriteToDirectory(destination, options);
        }
        else
        {
            using FileStream stream = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan);
            using IReader reader = ReaderFactory.OpenReader(stream);
            reader.WriteAllToDirectory(destination, options);
        }

        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var db = new SqliteConnection($"Data Source={DatabasePath};Cache=Shared");
        await db.OpenAsync(ct).ConfigureAwait(false);

        // Used only by the one-time v1 -> v2 migration. Keeping the conversion inside
        // SQLite lets the existing catalogue be compacted without re-reading disc images.
        db.CreateFunction<string, byte[]>("sha1_blob", static value => Sha1Bytes(value));

        using (SqliteCommand pragmas = db.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            await pragmas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int version = await ReadSchemaVersionAsync(db, ct).ConfigureAwait(false);
        if (version == 0 && await LooksLikeSchemaV1Async(db, ct).ConfigureAwait(false))
            version = 1;

        if (version == 0)
        {
            await CreateSchemaV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 1)
        {
            await MigrateSchemaV1ToV2Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV2ToV3Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 2)
        {
            await MigrateSchemaV2ToV3Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 3)
        {
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported SkeleTool SHA-1 catalogue schema version {version}.");
        }
        else
        {
            await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
        }

        return db;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand exists = db.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='meta')";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return 0;

        using SqliteCommand version = db.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key='schema_version' LIMIT 1";
        object? value = await version.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null || value is DBNull || !int.TryParse(Convert.ToString(value), out int parsed) ? 0 : parsed;
    }

    private static async Task<bool> LooksLikeSchemaV1Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('files') WHERE name='sha1')";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0;
    }

    private static async Task CreateSchemaV4Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS roots(
 id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, active INTEGER NOT NULL DEFAULT 1,
 added_utc TEXT NOT NULL, last_scanned_utc TEXT, last_success_utc TEXT, last_error TEXT);
CREATE TABLE IF NOT EXISTS units(
 id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id), kind TEXT NOT NULL,
 current_path TEXT NOT NULL, relative_path TEXT NOT NULL, size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
 sha1 BLOB NOT NULL CHECK(length(sha1)=20), layout_hash TEXT NOT NULL DEFAULT '', present INTEGER NOT NULL DEFAULT 1,
 first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, missing_since_utc TEXT, last_scanned_utc TEXT,
 evidence_gathered INTEGER NOT NULL DEFAULT 0, evidence_gathered_utc TEXT, evidence_schema INTEGER);
CREATE TABLE IF NOT EXISTS images(
 id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id) ON DELETE CASCADE,
 entry_path TEXT NOT NULL, display_name TEXT NOT NULL, source_offset INTEGER NOT NULL, source_length INTEGER NOT NULL,
 image_sha1 BLOB NOT NULL CHECK(length(image_sha1)=20), volume_identifier TEXT, image_kind TEXT, scanner_kind TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS hashes(
 id INTEGER PRIMARY KEY, sha1 BLOB NOT NULL CHECK(length(sha1)=20), size INTEGER NOT NULL,
 UNIQUE(sha1,size));
CREATE TABLE IF NOT EXISTS files(
 id INTEGER PRIMARY KEY, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 relative_path TEXT NOT NULL, hash_id INTEGER NOT NULL REFERENCES hashes(id), image_lba INTEGER, image_extents TEXT);
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','4');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaV4IndexesAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_units_root ON units(root_id,present);
CREATE INDEX IF NOT EXISTS ix_units_identity ON units(kind,sha1,layout_hash);
CREATE INDEX IF NOT EXISTS ix_images_unit ON images(unit_id);
CREATE INDEX IF NOT EXISTS ix_files_hash ON files(hash_id,image_id);
CREATE INDEX IF NOT EXISTS ix_files_image ON files(image_id);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaV2IndexesAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_units_root ON units(root_id,present);
CREATE INDEX IF NOT EXISTS ix_units_identity ON units(kind,sha1,layout_hash);
CREATE INDEX IF NOT EXISTS ix_images_unit ON images(unit_id);
CREATE INDEX IF NOT EXISTS ix_files_hash ON files(hash_id,image_id);
CREATE INDEX IF NOT EXISTS ix_files_image ON files(image_id);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task MigrateSchemaV1ToV2Async(SqliteConnection db, CancellationToken ct)
    {
        // The old catalogue may be hundreds of MiB. Migrate in-place from its already
        // calculated hashes, then VACUUM once so the obsolete text/hash index pages are
        // actually returned to the filesystem. No disc/archive content is rescanned.
        using (SqliteCommand fkOff = db.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys=OFF";
            await fkOff.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            using SqliteCommand cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
ALTER TABLE files RENAME TO files_v1;
ALTER TABLE images RENAME TO images_v1;
ALTER TABLE units RENAME TO units_v1;

CREATE TABLE units(
 id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id), kind TEXT NOT NULL,
 current_path TEXT NOT NULL, relative_path TEXT NOT NULL, size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
 sha1 BLOB NOT NULL CHECK(length(sha1)=20), layout_hash TEXT NOT NULL DEFAULT '', present INTEGER NOT NULL DEFAULT 1,
 first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, missing_since_utc TEXT, last_scanned_utc TEXT);
CREATE TABLE images(
 id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id) ON DELETE CASCADE,
 entry_path TEXT NOT NULL, display_name TEXT NOT NULL, source_offset INTEGER NOT NULL, source_length INTEGER NOT NULL,
 image_sha1 BLOB NOT NULL CHECK(length(image_sha1)=20), volume_identifier TEXT, image_kind TEXT, scanner_kind TEXT NOT NULL);
CREATE TABLE hashes(
 id INTEGER PRIMARY KEY, sha1 BLOB NOT NULL CHECK(length(sha1)=20), size INTEGER NOT NULL,
 UNIQUE(sha1,size));
CREATE TABLE files(
 id INTEGER PRIMARY KEY, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 relative_path TEXT NOT NULL, hash_id INTEGER NOT NULL REFERENCES hashes(id), image_lba INTEGER);

INSERT INTO units(id,root_id,kind,current_path,relative_path,size,mtime_ticks,sha1,layout_hash,present,first_seen_utc,last_seen_utc,missing_since_utc,last_scanned_utc)
 SELECT id,root_id,kind,current_path,relative_path,size,mtime_ticks,sha1_blob(sha1),layout_hash,present,first_seen_utc,last_seen_utc,missing_since_utc,last_scanned_utc FROM units_v1;
INSERT INTO images(id,unit_id,entry_path,display_name,source_offset,source_length,image_sha1,volume_identifier,image_kind,scanner_kind)
 SELECT id,unit_id,entry_path,display_name,source_offset,source_length,sha1_blob(image_sha1),volume_identifier,image_kind,scanner_kind FROM images_v1;
INSERT INTO hashes(sha1,size)
 SELECT DISTINCT sha1_blob(sha1),size FROM files_v1;
INSERT INTO files(id,image_id,relative_path,hash_id,image_lba)
 SELECT f.id,f.image_id,f.relative_path,h.id,f.image_lba
 FROM files_v1 f JOIN hashes h ON h.sha1=sha1_blob(f.sha1) AND h.size=f.size;

DROP TABLE files_v1;
DROP TABLE images_v1;
DROP TABLE units_v1;
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','2');";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EnsureSchemaV2IndexesAsync(db, ct).ConfigureAwait(false);
        using (SqliteCommand fkOn = db.CreateCommand())
        {
            fkOn.CommandText = "PRAGMA foreign_keys=ON";
            await fkOn.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using (SqliteCommand checkpoint = db.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using (SqliteCommand compact = db.CreateCommand())
        {
            compact.CommandText = "VACUUM";
            await compact.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task MigrateSchemaV3ToV4Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE files ADD COLUMN image_extents TEXT;
-- v3 stored only one LBA per ISO9660 file, so multi-extent hashes from those scans
-- cannot be trusted. Force filesystem-image units through one fresh scan; unchanged
-- archive/direct identity is retained, but their image contents will be re-indexed.
UPDATE units SET last_scanned_utc=NULL WHERE id IN (SELECT DISTINCT unit_id FROM images WHERE scanner_kind='ISO9660');
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','4');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }

    private static string SerializeImageExtents(IReadOnlyList<SkeletonSourceImageExtent> extents)
        => string.Join(";", extents.Select(extent => $"{extent.Lba}:{extent.Length}"));

    private static IReadOnlyList<SkeletonSourceImageExtent>? ParseImageExtents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new List<SkeletonSourceImageExtent>();
        foreach (string item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = item.IndexOf(':');
            if (colon <= 0 || !long.TryParse(item[..colon], out long lba) || !long.TryParse(item[(colon + 1)..], out long length) || lba < 0 || length < 0)
                return null;
            result.Add(new SkeletonSourceImageExtent(lba, length));
        }
        return result.Count == 0 ? null : result;
    }

    private static async Task MigrateSchemaV2ToV3Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE units ADD COLUMN evidence_gathered INTEGER NOT NULL DEFAULT 0;
ALTER TABLE units ADD COLUMN evidence_gathered_utc TEXT;
ALTER TABLE units ADD COLUMN evidence_schema INTEGER;
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','3');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }

    private async Task<ExistingUnit?> FindUnchangedUnitAsync(long rootId, string rel, string kind, long size, long ticks, string layout, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,sha1 FROM units WHERE root_id=$r AND relative_path=$p COLLATE NOCASE AND kind=$k AND size=$s AND mtime_ticks=$m AND layout_hash=$l AND present=1 AND last_scanned_utc IS NOT NULL ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$r", rootId); cmd.Parameters.AddWithValue("$p", rel); cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$s", size); cmd.Parameters.AddWithValue("$m", ticks); cmd.Parameters.AddWithValue("$l", layout);
            await using SqliteDataReader rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await rd.ReadAsync(ct).ConfigureAwait(false) ? new ExistingUnit(rd.GetInt64(0), Sha1Hex(rd.GetFieldValue<byte[]>(1))) : null;
        }
        finally { Gate.Release(); }
    }

    private async Task<ExistingUnit?> FindUnitByIdentityAsync(string kind, string sha1, string layout, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,sha1 FROM units WHERE kind=$k AND sha1=$h AND layout_hash=$l AND last_scanned_utc IS NOT NULL ORDER BY present DESC,last_seen_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.Add("$h", SqliteType.Blob).Value = Sha1Bytes(sha1); cmd.Parameters.AddWithValue("$l", layout);
            await using SqliteDataReader rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await rd.ReadAsync(ct).ConfigureAwait(false) ? new ExistingUnit(rd.GetInt64(0), Sha1Hex(rd.GetFieldValue<byte[]>(1))) : null;
        }
        finally { Gate.Release(); }
    }

    private async Task<bool> UnitHasFilesAsync(long id, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false); using SqliteCommand cmd=db.CreateCommand(); cmd.CommandText="SELECT EXISTS(SELECT 1 FROM files f JOIN images i ON i.id=f.image_id WHERE i.unit_id=$id)"; cmd.Parameters.AddWithValue("$id",id); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0; }
        finally { Gate.Release(); }
    }

    private async Task<long> InsertUnitAsync(long rootId,string kind,string path,string rel,long size,long ticks,string sha1,string layout,CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false); using SqliteCommand cmd=db.CreateCommand(); cmd.CommandText=@"INSERT INTO units(root_id,kind,current_path,relative_path,size,mtime_ticks,sha1,layout_hash,present,first_seen_utc,last_seen_utc,last_scanned_utc) VALUES($r,$k,$p,$rel,$s,$m,$h,$l,1,$n,$n,NULL); SELECT last_insert_rowid();"; cmd.Parameters.AddWithValue("$r",rootId);cmd.Parameters.AddWithValue("$k",kind);cmd.Parameters.AddWithValue("$p",Path.GetFullPath(path));cmd.Parameters.AddWithValue("$rel",rel);cmd.Parameters.AddWithValue("$s",size);cmd.Parameters.AddWithValue("$m",ticks);cmd.Parameters.Add("$h",SqliteType.Blob).Value=Sha1Bytes(sha1);cmd.Parameters.AddWithValue("$l",layout);cmd.Parameters.AddWithValue("$n",Now()); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)); }
        finally { Gate.Release(); }
    }

    private async Task TouchUnitAsync(long id,long rootId,string path,string rel,CancellationToken ct,long? size=null,long? ticks=null)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false); using SqliteCommand cmd=db.CreateCommand(); cmd.CommandText="UPDATE units SET root_id=$r,current_path=$p,relative_path=$rel,present=1,last_seen_utc=$n,missing_since_utc=NULL,size=COALESCE($s,size),mtime_ticks=COALESCE($m,mtime_ticks) WHERE id=$id";cmd.Parameters.AddWithValue("$r",rootId);cmd.Parameters.AddWithValue("$p",Path.GetFullPath(path));cmd.Parameters.AddWithValue("$rel",rel);cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$s",(object?)size??DBNull.Value);cmd.Parameters.AddWithValue("$m",(object?)ticks??DBNull.Value);cmd.Parameters.AddWithValue("$id",id);await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private async Task MarkSameLocationOlderUnitsMissingAsync(long rootId,string rel,long exceptId,CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false); using SqliteCommand cmd=db.CreateCommand(); cmd.CommandText="UPDATE units SET present=0,missing_since_utc=COALESCE(missing_since_utc,$n) WHERE root_id=$r AND relative_path=$p COLLATE NOCASE AND id<>$id AND present=1";cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$r",rootId);cmd.Parameters.AddWithValue("$p",rel);cmd.Parameters.AddWithValue("$id",exceptId);await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private async Task<long> InsertImageAndFilesAsync(
        long unitId,
        string entry,
        string display,
        long offset,
        long length,
        string imageSha1,
        SkeletoolCatalogueImageContent content,
        CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync(ct).ConfigureAwait(false);

            using SqliteCommand image = db.CreateCommand();
            image.Transaction = tx;
            image.CommandText = @"INSERT INTO images(unit_id,entry_path,display_name,source_offset,source_length,image_sha1,volume_identifier,image_kind,scanner_kind)
VALUES($u,$e,$d,$o,$l,$h,$v,$k,$s); SELECT last_insert_rowid();";
            image.Parameters.AddWithValue("$u", unitId);
            image.Parameters.AddWithValue("$e", entry);
            image.Parameters.AddWithValue("$d", display);
            image.Parameters.AddWithValue("$o", offset);
            image.Parameters.AddWithValue("$l", length);
            image.Parameters.Add("$h", SqliteType.Blob).Value = Sha1Bytes(imageSha1);
            image.Parameters.AddWithValue("$v", content.VolumeIdentifier);
            image.Parameters.AddWithValue("$k", (object?)content.ImageKind?.ToString() ?? DBNull.Value);
            image.Parameters.AddWithValue("$s", content.ScannerKind);
            long imageId = Convert.ToInt64(await image.ExecuteScalarAsync(ct).ConfigureAwait(false));

            using SqliteCommand hash = db.CreateCommand();
            hash.Transaction = tx;
            hash.CommandText = @"INSERT INTO hashes(sha1,size) VALUES($h,$z)
ON CONFLICT(sha1,size) DO NOTHING
RETURNING id;";
            SqliteParameter pHashSha1 = hash.Parameters.Add("$h", SqliteType.Blob);
            SqliteParameter pHashSize = hash.Parameters.Add("$z", SqliteType.Integer);

            using SqliteCommand findHash = db.CreateCommand();
            findHash.Transaction = tx;
            findHash.CommandText = "SELECT id FROM hashes WHERE sha1=$h AND size=$z";
            SqliteParameter pFindSha1 = findHash.Parameters.Add("$h", SqliteType.Blob);
            SqliteParameter pFindSize = findHash.Parameters.Add("$z", SqliteType.Integer);

            using SqliteCommand file = db.CreateCommand();
            file.Transaction = tx;
            file.CommandText = "INSERT INTO files(image_id,relative_path,hash_id,image_lba,image_extents) VALUES($i,$p,$h,$l,$x)";
            SqliteParameter pImage = file.Parameters.Add("$i", SqliteType.Integer);
            SqliteParameter pPath = file.Parameters.Add("$p", SqliteType.Text);
            SqliteParameter pHashId = file.Parameters.Add("$h", SqliteType.Integer);
            SqliteParameter pLba = file.Parameters.Add("$l", SqliteType.Integer);
            SqliteParameter pExtents = file.Parameters.Add("$x", SqliteType.Text);
            var localHashes = new Dictionary<(string Sha1, long Size), long>();
            foreach (SkeletoolCatalogueImageFile f in content.Files)
            {
                ct.ThrowIfCancellationRequested();
                string normalizedSha1 = f.Sha1.ToLowerInvariant();
                var key = (normalizedSha1, f.Size);
                if (!localHashes.TryGetValue(key, out long hashId))
                {
                    byte[] digest = Sha1Bytes(normalizedSha1);
                    pHashSha1.Value = digest;
                    pHashSize.Value = f.Size;
                    object? inserted = await hash.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (inserted is null || inserted is DBNull)
                    {
                        pFindSha1.Value = digest;
                        pFindSize.Value = f.Size;
                        object? existing = await findHash.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        if (existing is null || existing is DBNull)
                            throw new InvalidDataException("SHA-1 catalogue hash row could not be resolved after insert.");
                        hashId = Convert.ToInt64(existing);
                    }
                    else
                    {
                        hashId = Convert.ToInt64(inserted);
                    }
                    localHashes[key] = hashId;
                }

                pImage.Value = imageId;
                pPath.Value = f.RelativePath;
                pHashId.Value = hashId;
                pLba.Value = (object?)f.ImageLba ?? DBNull.Value;
                pExtents.Value = f.ImageExtents is { Count: > 0 } ? SerializeImageExtents(f.ImageExtents) : DBNull.Value;
                await file.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return imageId;
        }
        finally { Gate.Release(); }
    }

    private async Task SetUnitScannedAsync(long id,CancellationToken ct)
    { await Gate.WaitAsync(ct).ConfigureAwait(false); try { await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false);using SqliteCommand cmd=db.CreateCommand();cmd.CommandText="UPDATE units SET last_scanned_utc=$n,last_seen_utc=$n,present=1,missing_since_utc=NULL WHERE id=$id";cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$id",id);await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);} finally{Gate.Release();} }

    private async Task<IReadOnlyList<long>> GetPresentUnitIdsForLocationAsync(long rootId, string relativePath, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM units WHERE root_id=$r AND relative_path=$p COLLATE NOCASE AND present=1";
            cmd.Parameters.AddWithValue("$r", rootId);
            cmd.Parameters.AddWithValue("$p", relativePath);
            var ids = new List<long>();
            await using SqliteDataReader rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false)) ids.Add(rd.GetInt64(0));
            return ids;
        }
        finally { Gate.Release(); }
    }

    private async Task<int> MarkMissingAfterSuccessfulEnumerationAsync(long rootId,HashSet<long> seen,CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false);
            string ids=seen.Count==0?"-1":string.Join(',',seen);
            using SqliteCommand cmd=db.CreateCommand();
            cmd.CommandText=$"UPDATE units SET present=0,missing_since_utc=COALESCE(missing_since_utc,$n) WHERE root_id=$r AND present=1 AND id NOT IN ({ids}); SELECT changes();";
            cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$r",rootId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        finally{Gate.Release();}
    }

    private async Task UpdateRootSuccessAsync(long id,CancellationToken ct)=>await UpdateRootAsync(id,true,null,ct).ConfigureAwait(false);
    private async Task UpdateRootFailureAsync(long id,string error,CancellationToken ct)=>await UpdateRootAsync(id,false,error,ct).ConfigureAwait(false);
    private async Task UpdateRootCompletedWithErrorsAsync(long id,string error,CancellationToken ct)
    { await Gate.WaitAsync(ct).ConfigureAwait(false);try{await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false);using SqliteCommand cmd=db.CreateCommand();cmd.CommandText="UPDATE roots SET last_scanned_utc=$n,last_success_utc=$n,last_error=$e WHERE id=$id";cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$e",error);cmd.Parameters.AddWithValue("$id",id);await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}finally{Gate.Release();} }
    private async Task UpdateRootAsync(long id,bool success,string? error,CancellationToken ct)
    { await Gate.WaitAsync(ct).ConfigureAwait(false);try{await using SqliteConnection db=await OpenAsync(ct).ConfigureAwait(false);using SqliteCommand cmd=db.CreateCommand();cmd.CommandText=success?"UPDATE roots SET last_scanned_utc=$n,last_success_utc=$n,last_error=NULL WHERE id=$id":"UPDATE roots SET last_scanned_utc=$n,last_error=$e WHERE id=$id";cmd.Parameters.AddWithValue("$n",Now());cmd.Parameters.AddWithValue("$id",id);if(!success)cmd.Parameters.AddWithValue("$e",error??string.Empty);await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}finally{Gate.Release();} }

    private static void EnsureTemporaryCache()
    {
        if (Interlocked.Exchange(ref _cacheInitialized, 1) != 0) return;

        // v0.8.81 and earlier kept materialized images/files beside the EXE forever.
        // They are reproducible working files, so remove that legacy cache on upgrade.
        TryDeleteDirectory(LegacyCacheDirectory);

        try
        {
            Directory.CreateDirectory(TempCacheRoot);
            foreach (string directory in Directory.EnumerateDirectories(TempCacheRoot))
            {
                if (Path.GetFullPath(directory).Equals(Path.GetFullPath(SessionCacheDirectory), StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileName(directory);
                int underscore = name.IndexOf('_');
                if (underscore <= 0 || !int.TryParse(name[..underscore], out int pid) || !IsProcessAlive(pid))
                    TryDeleteDirectory(directory);
            }

            Directory.CreateDirectory(SessionCacheDirectory);
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteDirectory(SessionCacheDirectory);
        }
        catch
        {
            // Materialization methods will surface a useful I/O error if the OS temp
            // directory is genuinely unavailable. Cache cleanup itself must not stop
            // DumpToolbox from starting.
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static byte[] Sha1Bytes(string value)
    {
        if (!IsSha1(value)) throw new InvalidDataException($"Invalid SHA-1 value in catalogue: {value}");
        return Convert.FromHexString(value);
    }
    private static string Sha1Hex(byte[] value)
    {
        if (value.Length != 20) throw new InvalidDataException($"Invalid binary SHA-1 length in catalogue: {value.Length}");
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path,CancellationToken ct)
    { await using FileStream fs=new(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);using SHA1 sha=SHA1.Create();byte[] hash=await sha.ComputeHashAsync(fs,ct).ConfigureAwait(false);return Convert.ToHexString(hash).ToLowerInvariant(); }
    private static string HashText(string s)=>Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static bool IsSha1(string? s)=>s is {Length:40}&&s.All(Uri.IsHexDigit);
    private static bool IsDirectImage(string p)=>Path.GetExtension(p).Equals(".iso",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(p).Equals(".bin",StringComparison.OrdinalIgnoreCase);
    private static bool IsArchive(string p)=>new HashSet<string>(StringComparer.OrdinalIgnoreCase){".zip",".zipx",".7z",".rar",".arj",".ace",".arc",".zst",".gz",".bz2",".xz",".lz",".z",".tar",".tgz",".tbz",".tbz2",".txz",".tzst"}.Contains(Path.GetExtension(p));
    private static int CueSectorSize(string type)=>type.EndsWith("/2048",StringComparison.OrdinalIgnoreCase)?2048:type.EndsWith("/2336",StringComparison.OrdinalIgnoreCase)?2336:2352;
    private static string Norm(string p)=>p.Replace('\\','/');
    private static StringComparer PathComparer()=>OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
    private static string Now()=>DateTimeOffset.UtcNow.ToString("O");
    private static DateTimeOffset ParseDate(string s)=>DateTimeOffset.Parse(s,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ReadDate(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:ParseDate(r.GetString(i));
    private static string MakeTempDirectory(string prefix){string p=Path.Combine(Path.GetTempPath(),$"DumpToolbox_{prefix}_{Guid.NewGuid():N}");Directory.CreateDirectory(p);return p;}
    private static void TryDeleteFile(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}
    private static void TryDeleteDirectory(string p){try{if(Directory.Exists(p))Directory.Delete(p,true);}catch{}}
    private static string SafeName(string s){foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_');return string.IsNullOrWhiteSpace(s)?"image.bin":s;}
    private static bool IsUnderPath(string path,string parent){string p=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;string root=Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;return p.StartsWith(root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);}
    private static async Task CopyRangeAsync(string src,string dst,long offset,long length,CancellationToken ct){Directory.CreateDirectory(Path.GetDirectoryName(dst)!);await using FileStream input=new(src,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);await using FileStream output=new(dst,FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);input.Position=offset;byte[] buf=new byte[1024*1024];long remain=length;while(remain>0){int n=await input.ReadAsync(buf.AsMemory(0,(int)Math.Min(buf.Length,remain)),ct).ConfigureAwait(false);if(n<=0)throw new EndOfStreamException(src);await output.WriteAsync(buf.AsMemory(0,n),ct).ConfigureAwait(false);remain-=n;}}
    private static SkeletonContentEntry ResolveEntryGeometry(SkeletonContentEntry entry,long len){if(entry.DataLength==len)return entry;if(entry.AlternateIsoRecords is null)return entry;SkeletonAlternateIsoRecord[] c=entry.AlternateIsoRecords.Where(x=>x.DataLength==len).ToArray();return c.Length==1?entry with{ExtentLba=c[0].ExtentLba,DataLength=c[0].DataLength}:entry;}

    private sealed record ExistingUnit(long Id,string Sha1);
    private sealed record UnitScanResult(long UnitId,bool Skipped,int ImagesScanned,int FilesHashed,int Errors);
    private sealed record ScanOneImageResult(int Files);
    private sealed record DirectUnitPlan(string SourcePath,IReadOnlyList<ImagePlan> Images,string LayoutHash);
    private sealed record CatalogueWorkItem(bool IsArchive, string Path, DirectUnitPlan? DirectPlan);
    private sealed record ImagePlan(string SourcePath,string SourceEntryPath,string DisplayName,long SourceOffset,long SourceLength);
}
