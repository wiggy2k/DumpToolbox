using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    public Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchSourcesAsync(
        SkeletonInspectionResult inspection,
        string sourceDirectory,
        bool recursive,
        IProgress<SkeletonSourceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => MatchSourcesAsync(inspection, sourceDirectory, recursive, false, progress, cancellationToken);

    public Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchSourcesAsync(
        SkeletonInspectionResult inspection,
        string sourceDirectory,
        bool recursive,
        bool forceRehash,
        IProgress<SkeletonSourceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => MatchSourcesAsync(inspection, sourceDirectory, recursive, forceRehash, false, progress, cancellationToken);

    public Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchSourcesAsync(
        SkeletonInspectionResult inspection,
        string sourceDirectory,
        bool recursive,
        bool forceRehash,
        bool useHistoryDatabase,
        IProgress<SkeletonSourceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Run the whole scan on the thread pool.  This is intentional even though the
        // inner file reads are asynchronous: directory enumeration, cache pruning and
        // hashing setup are synchronous and can otherwise execute on Avalonia's UI
        // thread before the first incomplete await.
        return Task.Run(
            () => MatchSourcesCoreAsync(
                inspection,
                sourceDirectory,
                recursive,
                forceRehash,
                useHistoryDatabase,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchSourcesCoreAsync(
        SkeletonInspectionResult inspection,
        string sourceDirectory,
        bool recursive,
        bool forceRehash,
        bool useHistoryDatabase,
        IProgress<SkeletonSourceScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException("Choose a source-files folder.", nameof(sourceDirectory));

        string directory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Source folder not found: {directory}");

        if (inspection.SourceKind == SkeletonSourceKind.DiscImageCreator)
            return MatchDicSources(inspection, directory, recursive, progress, cancellationToken);

        var expected = new Dictionary<string, List<(SkeletonContentEntry Entry, bool Xa)>>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonContentEntry entry in inspection.Entries)
        {
            if (!entry.CanRestore)
                continue;

            // The canonical all-zero SYSTEM_AREA is satisfied by regenerating raw
            // sector protection data; it does not require an external source file.
            if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                string.Equals(entry.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(entry.Sha1) && !entry.Sha1.Equals(EmptySha1, StringComparison.OrdinalIgnoreCase))
                AddExpected(expected, entry.Sha1, entry, false);
            if (!string.IsNullOrWhiteSpace(entry.XaSha1) && !entry.XaSha1.Equals(EmptySha1, StringComparison.OrdinalIgnoreCase))
                AddExpected(expected, entry.XaSha1, entry, true);
        }

        int requiredTargetCount = expected.Values
            .SelectMany(v => v)
            .Select(v => v.Entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Redumper's normal (non-.XA) hashes are over the exact logical file/gap
        // length.  If no XA/Form2 alternate hashes are present, file length is
        // therefore a completely safe and very cheap pre-filter.  XA hashes are
        // different because they contain 2324-byte Form2 payloads, so disable the
        // size filter for those dumps rather than making assumptions.
        bool hasXaTargets = inspection.Entries.Any(e =>
            e.CanRestore &&
            !string.IsNullOrWhiteSpace(e.XaSha1) &&
            !e.XaSha1.Equals(EmptySha1, StringComparison.OrdinalIgnoreCase));

        HashSet<long> expectedLengths = inspection.Entries
            .Where(e => e.CanRestore &&
                        !e.IsEmpty &&
                        !(e.SpecialKind == SkeletonSpecialKind.SystemArea &&
                          string.Equals(e.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(e.Sha1) &&
                        e.DataLength > 0)
            .Select(e => e.DataLength)
            .ToHashSet();

        // Explicit SkeleTool source scans only need files whose logical lengths can satisfy
        // this skeleton. The global SHA-1 catalogue is populated independently in Settings.
        bool useLengthFilter = !hasXaTargets && expectedLengths.Count > 0;

        string cachePath = Path.Combine(directory, HashCacheFileName);
        Dictionary<string, HashCacheEntry> loadedCache = forceRehash
            ? new Dictionary<string, HashCacheEntry>(GetPathComparer())
            : await LoadHashCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);

        // Start with the existing cache so a non-recursive scan does not discard
        // useful hashes previously collected from subdirectories. Dead entries are
        // pruned cheaply before the updated cache is written.
        var updatedCache = new ConcurrentDictionary<string, HashCacheEntry>(loadedCache, GetPathComparer());
        PruneMissingCacheEntries(directory, updatedCache);

        SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        FileInfo[] files = Directory.EnumerateFiles(directory, "*", option)
            .Select(path => new FileInfo(path))
            .Where(info => !PathsEqual(info.FullName, inspection.SkeletonPath) &&
                           !PathsEqual(info.FullName, inspection.HashPath) &&
                           !PathsEqual(info.FullName, cachePath))
            .ToArray();

        long totalBytes = files.Sum(info => info.Length);
        long bytesProcessed = 0;
        long bytesHashed = 0;
        int filesProcessed = 0;
        int filesHashed = 0;
        int filesSkipped = 0;
        int filesCached = 0;
        var matches = new ConcurrentDictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);

        // Progress<T> created by Avalonia posts every Report call to the UI dispatcher.
        // Reporting once or twice for every tiny file can queue thousands of UI
        // callbacks and make Windows label the application as Not Responding even
        // though hashing is still making progress. Keep routine reports to ~10 Hz and
        // live match notifications to ~20 Hz; the final result refreshes every tree
        // entry so no match can be lost visually.
        long lastRoutineProgressTick = 0;
        long lastMatchProgressTick = 0;

        // A handful of concurrent readers removes much of the per-file open/close
        // latency on SSDs without creating the seek storm that an unbounded parallel
        // scan can cause on spinning disks.
        int hashWorkers = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = hashWorkers
        };

        try
        {
            await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
            {
                ct.ThrowIfCancellationRequested();

                string relativePath = NormalizeCacheRelativePath(Path.GetRelativePath(directory, file.FullName));

                // If all manifest entries are already satisfied, avoid hashing the rest
                // of a large source tree. Work already in flight may finish naturally.
                bool allFound = requiredTargetCount > 0 && matches.Count >= requiredTargetCount;
                bool skipForLength = useLengthFilter && !expectedLengths.Contains(file.Length);
                bool skip = allFound || skipForLength;

                string? sha1 = null;

                if (!skip && !forceRehash &&
                    loadedCache.TryGetValue(relativePath, out HashCacheEntry? cachedEntry) &&
                    cachedEntry.Length == file.Length &&
                    cachedEntry.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks &&
                    IsSha1(cachedEntry.Sha1))
                {
                    sha1 = cachedEntry.Sha1.ToLowerInvariant();
                    Interlocked.Increment(ref filesCached);

                    // Keep metadata canonical even if an older cache used a different
                    // representation of the relative path.
                    updatedCache[relativePath] = new HashCacheEntry
                    {
                        RelativePath = relativePath,
                        Length = file.Length,
                        LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                        Sha1 = sha1
                    };
                }
                else if (!skip)
                {
                    sha1 = await CalculateSha1Async(file.FullName, ct);
                    Interlocked.Add(ref bytesHashed, file.Length);
                    Interlocked.Increment(ref filesHashed);
                    updatedCache[relativePath] = new HashCacheEntry
                    {
                        RelativePath = relativePath,
                        Length = file.Length,
                        LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                        Sha1 = sha1
                    };
                }
                else
                {
                    Interlocked.Increment(ref filesSkipped);

                    // A stale cache entry must never survive just because this
                    // particular manifest allowed us to skip hashing the file.
                    if (loadedCache.TryGetValue(relativePath, out HashCacheEntry? staleCachedEntry) &&
                        (staleCachedEntry.Length != file.Length || staleCachedEntry.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks))
                    {
                        updatedCache.TryRemove(relativePath, out _);
                    }
                }

                long processedBytesNow = Interlocked.Add(ref bytesProcessed, file.Length);
                int processedFilesNow = Interlocked.Increment(ref filesProcessed);
                long hashedBytesNow = Interlocked.Read(ref bytesHashed);
                int hashedFilesNow = Volatile.Read(ref filesHashed);
                int skippedFilesNow = Volatile.Read(ref filesSkipped);
                int cachedFilesNow = Volatile.Read(ref filesCached);

                if (sha1 is not null && expected.TryGetValue(sha1, out List<(SkeletonContentEntry Entry, bool Xa)>? targets))
                {
                    foreach ((SkeletonContentEntry entry, bool xa) in targets)
                    {
                        SkeletonContentEntry matchedEntry = !xa
                            ? ResolveRedumperEntryGeometryForSourceLength(entry, file.Length)
                            : entry;
                        if (!xa && matchedEntry.DataLength != file.Length)
                            continue;

                        var candidate = new SkeletonSourceMatch(matchedEntry, file.FullName, sha1, xa);
                        SkeletonSourceMatch selected = matches.AddOrUpdate(
                            entry.Path,
                            candidate,
                            (_, existing) =>
                            {
                                // A freshly hashed source file is stronger evidence than a
                                // reusable history-database sighting. This is particularly
                                // important when duplicate ISO9660 paths have different
                                // record lengths: older history may have been bound to the
                                // collapsed/default geometry, while matchedEntry above has
                                // just resolved the exact geometry from the current file length.
                                if (existing.MatchMethod.StartsWith("SHA-1 catalogue", StringComparison.OrdinalIgnoreCase))
                                    return candidate;

                                // Preserve the existing preference for a normal SHA-1 match
                                // over an XA alternate when both identify the same logical entry.
                                return existing.IsXa && !xa ? candidate : existing;
                            });

                        // Only announce the candidate if it actually became the selected
                        // match for this ISO entry.
                        if (string.Equals(selected.SourcePath, candidate.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                            selected.IsXa == candidate.IsXa)
                        {
                            if (progress is not null && TryClaimProgressSlot(ref lastMatchProgressTick, 50))
                            {
                                progress.Report(new SkeletonSourceScanProgress(
                                    processedFilesNow,
                                    files.Length,
                                    processedBytesNow,
                                    totalBytes,
                                    file.FullName,
                                    entry.Path,
                                    file.FullName,
                                    xa,
                                    hashedBytesNow,
                                    hashedFilesNow,
                                    skippedFilesNow,
                                    cachedFilesNow));
                            }
                        }
                    }
                }

                if (progress is not null && TryClaimProgressSlot(ref lastRoutineProgressTick, 100))
                {
                    progress.Report(new SkeletonSourceScanProgress(
                        processedFilesNow,
                        files.Length,
                        processedBytesNow,
                        totalBytes,
                        file.FullName,
                        BytesHashed: hashedBytesNow,
                        FilesHashed: hashedFilesNow,
                        FilesSkipped: skippedFilesNow,
                        FilesCached: cachedFilesNow));
                }
            }).ConfigureAwait(false);

            progress?.Report(new SkeletonSourceScanProgress(
                Volatile.Read(ref filesProcessed),
                files.Length,
                Interlocked.Read(ref bytesProcessed),
                totalBytes,
                string.Empty,
                BytesHashed: Interlocked.Read(ref bytesHashed),
                FilesHashed: Volatile.Read(ref filesHashed),
                FilesSkipped: Volatile.Read(ref filesSkipped),
                FilesCached: Volatile.Read(ref filesCached)));
        }
        finally
        {
            // Cache persistence is best-effort: failure to update the acceleration
            // cache must never invalidate an otherwise successful source scan.
            await TrySaveHashCacheAsync(cachePath, updatedCache.Values, CancellationToken.None).ConfigureAwait(false);
        }

        return new Dictionary<string, SkeletonSourceMatch>(matches, StringComparer.OrdinalIgnoreCase);
    }


    public Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchSourceImageAsync(
        SkeletonInspectionResult inspection,
        string sourceImagePath,
        bool useHistoryDatabase,
        IProgress<SkeletonSourceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            if (inspection.SourceKind != SkeletonSourceKind.Redumper)
                throw new InvalidOperationException("Direct ISO/BIN source scanning is currently a Skeletool feature.");
            if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
                throw new FileNotFoundException("Source ISO/BIN image not found.", sourceImagePath);

            string imagePath = Path.GetFullPath(sourceImagePath);
            await using SkeletonImageReader reader = await SkeletonImageReader.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
            IsoTree tree = await ReadIsoTreeAsync(reader, cancellationToken).ConfigureAwait(false);

            var expected = new Dictionary<string, List<(SkeletonContentEntry Entry, bool Xa)>>(StringComparer.OrdinalIgnoreCase);
            foreach (SkeletonContentEntry entry in inspection.Entries.Where(e => e.CanRestore && !e.IsEmpty))
            {
                if (IsSha1(entry.Sha1)) AddExpected(expected, entry.Sha1!, entry, false);
                if (IsSha1(entry.XaSha1)) AddExpected(expected, entry.XaSha1!, entry, true);
            }

            var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = tree.Files.Sum(f => f.LogicalLength);
            long processedBytes = 0;
            int processed = 0;

            foreach (IsoFileExtent file in tree.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] payload;
                try
                {
                    if (file.LogicalLength > int.MaxValue)
                        throw new InvalidOperationException("Logical ISO/BIN source file is larger than the in-memory hash limit.");
                    payload = new byte[checked((int)file.LogicalLength)];
                    int writeOffset = 0;
                    foreach (SkeletonSourceImageExtent extent in file.LogicalExtents)
                    {
                        if (extent.Length > int.MaxValue)
                            throw new InvalidOperationException("Logical ISO/BIN source extent is larger than the in-memory hash limit.");
                        byte[] part = await reader.ReadForm1BytesAsync(checked((uint)extent.Lba), checked((uint)extent.Length), cancellationToken).ConfigureAwait(false);
                        Buffer.BlockCopy(part, 0, payload, writeOffset, part.Length);
                        writeOffset += part.Length;
                    }
                }
                catch (InvalidOperationException)
                {
                    processedBytes += file.LogicalLength; processed++;
                    progress?.Report(new SkeletonSourceScanProgress(processed, tree.Files.Count, processedBytes, totalBytes, file.Path, FilesSkipped: 1));
                    continue;
                }

                string sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
                if (expected.TryGetValue(sha1, out List<(SkeletonContentEntry Entry, bool Xa)>? targets))
                {
                    foreach ((SkeletonContentEntry entry, bool xa) in targets)
                    {
                        // A normal ISO9660 file stream is Form1/user-data. XA alternate
                        // hashes require a raw 2324-byte Form2 payload and are therefore
                        // not claimed by this generic image-file reader.
                        if (xa) continue;
                        SkeletonContentEntry matchedEntry = ResolveRedumperEntryGeometryForSourceLength(entry, file.LogicalLength);
                        if (matchedEntry.DataLength != file.LogicalLength)
                            continue;
                        matches[entry.Path] = new SkeletonSourceMatch(matchedEntry, imagePath, sha1, false,
                            "ISO/BIN image logical file SHA1", file.Path, file.Lba, file.LogicalLength, SourceImageExtents: file.LogicalExtents);
                        progress?.Report(new SkeletonSourceScanProgress(processed, tree.Files.Count, processedBytes, totalBytes,
                            file.Path, entry.Path, $"{imagePath}::{file.Path}", false));
                    }
                }

                processedBytes += file.LogicalLength; processed++;
                progress?.Report(new SkeletonSourceScanProgress(processed, tree.Files.Count, processedBytes, totalBytes, file.Path,
                    BytesHashed: processedBytes, FilesHashed: processed));
            }

            return (IReadOnlyDictionary<string, SkeletonSourceMatch>)matches;
        }, cancellationToken);


    private static IReadOnlyDictionary<string, SkeletonSourceMatch> MatchDicSources(
        SkeletonInspectionResult inspection,
        string directory,
        bool recursive,
        IProgress<SkeletonSourceScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        _ = recursive; // DIC mode always scans all descendants; retained for API compatibility.

        JolietNamingProfile? masteringNamingProfile = JolietNamingRuleService.ResolveForInspection(inspection, out _, out _);

        // DIC source matching is intentionally strict.  Only the primary ISO9660
        // relative path/filename and exact byte length are used.  Matching is
        // case-insensitive and path separators are normalised, but there are no
        // 8.3 aliases, prefix guesses, timestamp tie-breakers, or size-only fallbacks.
        SearchOption option = SearchOption.AllDirectories;
        var excluded = new HashSet<string>(GetPathComparer());
        excluded.Add(Path.GetFullPath(inspection.SkeletonPath));
        if (!string.IsNullOrWhiteSpace(inspection.HashPath))
            excluded.Add(Path.GetFullPath(inspection.HashPath));
        if (inspection.CompanionPaths is not null)
        {
            foreach (string path in inspection.CompanionPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    excluded.Add(Path.GetFullPath(path));
            }
        }
        excluded.Add(Path.Combine(directory, HashCacheFileName));

        IsoExtractionManifest? extractorManifest = IsoExtractionManifestService.TryLoad(directory);
        bool extractorManifestMatches = extractorManifest is not null &&
            IsoExtractionManifestService.MatchesInspection(extractorManifest, inspection, out _);
        bool extractorManifestPayloadOnly = extractorManifest is not null &&
            !extractorManifestMatches &&
            IsoExtractionManifestService.IsPayloadOnlyCompatible(extractorManifest, inspection, out _);

        FileInfo[] files = Directory.EnumerateFiles(directory, "*", option)
            .Select(path => new FileInfo(path))
            .Where(info => !excluded.Contains(Path.GetFullPath(info.FullName)))
            .Where(info => !Path.GetRelativePath(directory, info.FullName)
                .Replace('\\', '/')
                .StartsWith(IsoExtractionManifestService.PrivateDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var bySizeAndPath = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<FileInfo>>();
        var relativePaths = new Dictionary<string, string>(GetPathComparer());
        foreach (FileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = NormalizeDicRelativePath(Path.GetRelativePath(directory, file.FullName));
            relativePaths[file.FullName] = relativePath;
            string key = DicPathSizeKey(relativePath, file.Length);
            AddCandidate(bySizeAndPath, key, file);
            if (!bySize.TryGetValue(file.Length, out List<FileInfo>? sameSize))
                bySize[file.Length] = sameSize = new List<FileInfo>();
            sameSize.Add(file);
        }

        SkeletonContentEntry[] requiredEntries = inspection.Entries
            .Where(e => e.CanRestore && e.RequiresSource && !e.IsEmpty)
            .ToArray();
        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);

        // v0.3.2: one retained source payload may satisfy only one distinct primary
        // physical extent. Reuse is permitted only for explicit shared-extent aliases.
        // This mirrors the DICSimulator v0.0.12 safety rule and prevents a strong early
        // match from being silently reused by a later fuzzy/ordinal fallback.
        var sourceClaims = new Dictionary<string, HashSet<uint>>(GetPathComparer());

        bool SourceAvailableForEntry(FileInfo source, SkeletonContentEntry target)
        {
            string key = Path.GetFullPath(source.FullName);
            return !sourceClaims.TryGetValue(key, out HashSet<uint>? extents) || extents.Contains(target.ExtentLba);
        }

        void ClaimSource(FileInfo source, SkeletonContentEntry target)
        {
            string key = Path.GetFullPath(source.FullName);
            if (!sourceClaims.TryGetValue(key, out HashSet<uint>? extents))
                sourceClaims[key] = extents = new HashSet<uint>();
            extents.Add(target.ExtentLba);
        }

        string GetPrimaryIsoParent(SkeletonContentEntry entry)
        {
            string primaryPath = !string.IsNullOrWhiteSpace(entry.IsoOriginalPath)
                ? entry.IsoOriginalPath!
                : entry.Path;
            return GetDicParentPath(NormalizeDicRelativePath(primaryPath)).ToUpperInvariant();
        }

        bool TryResolveLevel1CollisionFamilyByRank(SkeletonContentEntry target, out FileInfo? selected)
        {
            selected = null;
            if ((target.IsoFileFlags & 0x04) != 0 || target.SpecialKind != SkeletonSpecialKind.None)
                return false;

            string targetParent = GetPrimaryIsoParent(target);
            SkeletonContentEntry[] targetFamily = requiredEntries
                .Where(entry => entry.SpecialKind == SkeletonSpecialKind.None)
                .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                .Where(entry => GetPrimaryIsoParent(entry).Equals(targetParent, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.ExtentLba)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();

            if (targetFamily.Length < 4 ||
                targetFamily.Select(entry => entry.ExtentLba).Distinct().Count() != targetFamily.Length)
                return false;

            int targetRank = Array.FindIndex(targetFamily, entry => ReferenceEquals(entry, target) ||
                entry.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase));
            if (targetRank < 0)
                return false;

            var qualifyingGroups = new List<FileInfo[]>();
            foreach (IGrouping<string, FileInfo> sourceParentGroup in files
                         .Where(file => relativePaths.TryGetValue(file.FullName, out string? relative) &&
                                        JolietParentProjectsToIsoParent(GetDicParentPath(relative), targetParent))
                         .GroupBy(file => GetDicParentPath(relativePaths[file.FullName]), StringComparer.OrdinalIgnoreCase))
            {
                FileInfo[] sourceFamily = sourceParentGroup
                    .OrderBy(file => GetDicFilename(relativePaths[file.FullName]), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => GetDicFilename(relativePaths[file.FullName]), StringComparer.Ordinal)
                    .ToArray();
                if (sourceFamily.Length != targetFamily.Length)
                    continue;

                string[] projectedLeaves = sourceFamily
                    .Select(file => ProjectJolietComponentToIsoLevel1(GetDicFilename(relativePaths[file.FullName]), true))
                    .ToArray();
                bool hasLevel1Collision = projectedLeaves
                    .GroupBy(leaf => leaf, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1);
                if (!hasLevel1Collision)
                    continue;

                // The allocator sequence may span mixed file sizes.  Before trusting rank,
                // require the entire source/target sibling sequence to agree on exact lengths.
                // Size is therefore corroborating evidence for every predicted pair rather than
                // a boundary that incorrectly splits one allocator run into separate families.
                if (!sourceFamily.Select((file, i) => file.Length == targetFamily[i].DataLength).All(equal => equal))
                    continue;

                var anchorRanks = new List<int>();
                var mismatchRanks = new List<int>();
                for (int i = 0; i < sourceFamily.Length; i++)
                {
                    string targetLeaf = StripIsoVersionForMatching(GetDicFilename(
                        !string.IsNullOrWhiteSpace(targetFamily[i].IsoOriginalPath)
                            ? targetFamily[i].IsoOriginalPath!
                            : targetFamily[i].Path));
                    if (projectedLeaves[i].Equals(targetLeaf, StringComparison.OrdinalIgnoreCase))
                        anchorRanks.Add(i);
                    else
                        mismatchRanks.Add(i);
                }

                // Require independent direct-projection anchors on both sides of the
                // displaced run.  This proves the lexical/extent rank relationship locally
                // and prevents an unbracketed collision at one edge from becoming an
                // extrapolation rule.
                if (anchorRanks.Count < 2 || mismatchRanks.Count == 0 ||
                    !anchorRanks.Any(rank => rank < mismatchRanks[0]) ||
                    !anchorRanks.Any(rank => rank > mismatchRanks[^1]))
                    continue;

                qualifyingGroups.Add(sourceFamily);
            }

            if (qualifyingGroups.Count != 1)
                return false;

            FileInfo candidate = qualifyingGroups[0][targetRank];
            if (!SourceAvailableForEntry(candidate, target))
                return false;
            if (target.RecordingTime is DateTimeOffset timestamp &&
                !SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
            {
                // Timestamp is allowed to reject a rank prediction, but never to create it.
                return false;
            }

            selected = candidate;
            return true;
        }

        Dictionary<string, string> BuildProvenParentMap()
        {
            // A successfully matched child proves that its source parent directory and
            // primary ISO9660 parent directory correspond.  Only retain mutual-unique
            // parent pairs so one source directory can never become an accidental anchor
            // for two different primary directories (or vice versa).
            var observations = matches.Values
                .Where(match => match.Entry.SpecialKind == SkeletonSpecialKind.None)
                .Where(match => (match.Entry.IsoFileFlags & 0x04) == 0)
                .Select(match =>
                {
                    string? relative = !string.IsNullOrWhiteSpace(match.SourceRelativePath)
                        ? NormalizeDicRelativePath(match.SourceRelativePath!)
                        : relativePaths.TryGetValue(match.SourcePath, out string? discovered)
                            ? NormalizeDicRelativePath(discovered)
                            : null;
                    return relative is null
                        ? null
                        : new
                        {
                            TargetParent = GetPrimaryIsoParent(match.Entry),
                            SourceParent = GetDicParentPath(relative).ToUpperInvariant()
                        };
                })
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();

            var targetToSources = observations
                .GroupBy(item => item.TargetParent, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.SourceParent).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var sourceToTargets = observations
                .GroupBy(item => item.SourceParent, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.TargetParent).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string targetParent, string[] sourceParents) in targetToSources)
            {
                if (sourceParents.Length != 1)
                    continue;
                string sourceParent = sourceParents[0];
                if (!sourceToTargets.TryGetValue(sourceParent, out string[]? targetParents) ||
                    targetParents.Length != 1 ||
                    !targetParents[0].Equals(targetParent, StringComparison.OrdinalIgnoreCase))
                    continue;
                result[targetParent] = sourceParent;
            }
            return result;
        }

        int processedEntries = 0;
        long lastProgressTick = 0;

        progress?.Report(new SkeletonSourceScanProgress(
            0,
            requiredEntries.Length,
            0,
            requiredEntries.Length,
            string.Empty));

        SkeletonContentEntry[] ordinaryPositiveEntries = requiredEntries
            .Where(entry => entry.SpecialKind == SkeletonSpecialKind.None)
            .Where(entry => (entry.IsoFileFlags & 0x02) == 0)
            .Where(entry => entry.DataLength > 0)
            .ToArray();
        bool narrowJolietWarningSignature = ordinaryPositiveEntries.Length == 1 &&
            ordinaryPositiveEntries[0].DataLength == JolietCdWarningTemplate.Length &&
            GetDicEntryAliases(ordinaryPositiveEntries[0])
                .Any(path => NormalizeDicRelativePath(path).Equals("JOLIETCD.TXT", StringComparison.OrdinalIgnoreCase)) &&
            SkeletonHasJolietDescriptor(inspection);

        foreach (SkeletonContentEntry entry in requiredEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo? selected = null;
            string matchMethod = "ISO9660 exact relative path+filename+size";

            if (narrowJolietWarningSignature &&
                ReferenceEquals(entry, ordinaryPositiveEntries[0]) &&
                !files.Any(file =>
                    file.Length == entry.DataLength &&
                    relativePaths.TryGetValue(file.FullName, out string? relative) &&
                    NormalizeDicRelativePath(relative).Equals("JOLIETCD.TXT", StringComparison.OrdinalIgnoreCase)))
            {
                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    "<generated:JOLIETCD.TXT>",
                    string.Empty,
                    false,
                    "Deterministic primary-only Joliet warning template",
                    "JOLIETCD.TXT",
                    SourceLength: JolietCdWarningTemplate.Length,
                    GeneratedPayload: JolietCdWarningTemplate);
                processedEntries++;
                continue;
            }

            if ((extractorManifestMatches || extractorManifestPayloadOnly) && extractorManifest is not null)
            {
                string expectedIsoPath = NormalizeDicRelativePath(entry.IsoOriginalPath ?? entry.Path);

                // The ISO Extractor keeps duplicate/associated records under its private
                // .dumptoolbox_iso_records directory and describes their original ISO
                // identity in the manifest.  Never treat that directory as a loose source
                // tree: resolve it through manifest evidence instead.
                IsoExtractionManifestFile[] compatibleManifestRecords = extractorManifest.Files
                    .Where(record => NormalizeDicRelativePath(record.IsoPath).Equals(expectedIsoPath, StringComparison.OrdinalIgnoreCase))
                    .Where(record => record.DataLength == entry.DataLength)
                    .Where(record => record.FileFlags == entry.IsoFileFlags)
                    .ToArray();

                IsoExtractionManifestFile[] manifestMatches = extractorManifestMatches && entry.IsoRecordExtentLba is uint recordLba
                    ? compatibleManifestRecords.Where(record => record.ExtentLba == recordLba).ToArray()
                    : Array.Empty<IsoExtractionManifestFile>();

                // Full-identity mode may prefer exact extractor LBA evidence.  Payload-only
                // mode explicitly ignores extractor geometry and accepts only one unique
                // manifest record by DIC ISO path + exact size + flags.
                IsoExtractionManifestFile? manifestRecord = extractorManifestMatches && manifestMatches.Length == 1
                    ? manifestMatches[0]
                    : compatibleManifestRecords.Length == 1
                        ? compatibleManifestRecords[0]
                        : null;

                if (manifestRecord is not null &&
                    !(entry.ContainsMode2Form2 && extractorManifest.SourceSectorSize == CookedSectorSize))
                {
                    string manifestPath = Path.GetFullPath(Path.Combine(directory, manifestRecord.ExtractedRelativePath));
                    if (File.Exists(manifestPath) && new FileInfo(manifestPath).Length == entry.DataLength)
                    {
                        selected = new FileInfo(manifestPath);
                        bool exactRecord = extractorManifestMatches && entry.IsoRecordExtentLba is uint exactLba && manifestRecord.ExtentLba == exactLba;
                        string namespaceEvidence = extractorManifest.Version >= 2 && !string.IsNullOrWhiteSpace(manifestRecord.JolietPath)
                            ? $"; mapped Joliet path '{manifestRecord.JolietPath}'"
                            : string.Empty;
                        if (extractorManifestPayloadOnly)
                        {
                            matchMethod = (entry.IsoFileFlags & 0x04) != 0
                                ? "DumpToolbox ISO Extractor manifest — payload-only Associated record by path+size+flags"
                                : "DumpToolbox ISO Extractor manifest — payload-only ISO record by path+size+flags";
                        }
                        else
                        {
                            matchMethod = (entry.IsoFileFlags & 0x04) != 0
                                ? exactRecord
                                    ? "DumpToolbox ISO Extractor manifest — exact Associated record"
                                    : "DumpToolbox ISO Extractor manifest — unique Associated record by path+size+flags"
                                : exactRecord
                                    ? "DumpToolbox ISO Extractor manifest — exact ISO record"
                                    : "DumpToolbox ISO Extractor manifest — unique ISO record by path+size+flags";
                        }
                        matchMethod += namespaceEvidence;
                    }
                }
            }

            if (selected is null && (entry.IsoFileFlags & 0x04) == 0)
            foreach (string alias in GetDicEntryAliases(entry))
            {
                string expectedPath = NormalizeDicRelativePath(alias);
                string expectedName = GetDicFilename(expectedPath);
                string key = DicPathSizeKey(expectedPath, entry.DataLength);

                if (!bySizeAndPath.TryGetValue(key, out List<FileInfo>? candidates))
                    continue;

                FileInfo[] exact = candidates
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                // On case-sensitive filesystems it is possible to have two different
                // files whose paths differ only by case.  Because DIC matching is
                // deliberately case-insensitive, that situation is ambiguous and is
                // left unresolved rather than guessing.
                if (exact.Length == 1)
                {
                    FileInfo exactCandidate = exact[0];
                    bool competingProjection = bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSizeForExactGuard) &&
                        sameSizeForExactGuard.Any(candidate =>
                            !GetPathComparer().Equals(Path.GetFullPath(candidate.FullName), Path.GetFullPath(exactCandidate.FullName)) &&
                            SourceAvailableForEntry(candidate, entry) &&
                            relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                            JolietPathProjectsToIsoPath(relative, expectedPath, masteringNamingProfile));

                    // DICSimulator v0.0.12 exposed a dangerous case where a literal Joliet
                    // sibling and another same-size long name can both collapse onto the
                    // same primary ISO identifier. Defer rather than letting the literal
                    // spelling win merely because it happens to exist on disk.
                    if (!competingProjection)
                    {
                        selected = exactCandidate;
                        break;
                    }
                }
            }

            // Some ISO9660 Level-1 collision allocators renumber an entire sibling run
            // after one long Joliet name collides with an earlier 8.3 projection. Ultimate
            // Solitaire 1000 demonstrates a same-size run; Vojna s terrorom demonstrates
            // that the allocator sequence can span mixed file sizes. A literal projection+size
            // match is therefore actively misleading for shifted members. Before using that
            // rule, recognise a complete same-parent sibling sequence whose source lexical
            // order is independently anchored to target physical extent order in multiple
            // places, with exact size agreement required for every rank-paired member.
            if (selected is null && TryResolveLevel1CollisionFamilyByRank(entry, out FileInfo? rankedCollisionSource))
            {
                selected = rankedCollisionSource;
                matchMethod = "Joliet Level-1 collision directory: source lexical rank -> DIC extent rank + exact per-pair size";
            }

            // Normal folder copies of Joliet discs expose the supplementary/user-visible
            // names, not necessarily the primary ISO9660 8.3 identifiers recorded by DIC.
            // If the exact primary path was not present, project each same-sized source
            // path to conservative ISO9660 Level-1 components and accept it only when the
            // resulting path identifies this one DIC record unambiguously.  This allows
            // examples such as BlackMirror.ico -> BLACKMIR.ICO and Setup-1.bin ->
            // SETUP_1.BIN without weakening the exact-size requirement.
            if (selected is null && (entry.IsoFileFlags & 0x04) == 0 &&
                bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sizeCandidates))
            {
                string[] expectedAliases = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .ToArray();

                FileInfo[] projected = sizeCandidates
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        expectedAliases.Any(expected => JolietPathProjectsToIsoPath(relative, expected, masteringNamingProfile)))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                bool timestampDisambiguated = false;
                if (projected.Length > 1 && entry.RecordingTime is DateTimeOffset expectedRecordingTime)
                {
                    FileInfo[] timestampMatches = projected
                        .Where(candidate => SourceTimestampMatchesDicRecordingTime(candidate, expectedRecordingTime))
                        .ToArray();
                    if (timestampMatches.Length == 1)
                    {
                        projected = timestampMatches;
                        timestampDisambiguated = true;
                    }
                }

                if (projected.Length == 1)
                {
                    // Reject reverse ambiguity too: one Joliet source path must not be a
                    // plausible projection for another same-sized primary ISO record. If
                    // a ~N alias group was resolved by an exact source/DIC timestamp, apply
                    // the same timestamp constraint to the reverse check; the timestamp is
                    // evidence only after path+size compatibility has already been proven.
                    string relative = relativePaths[projected[0].FullName];
                    int compatibleEntries = requiredEntries.Count(other =>
                        (other.IsoFileFlags & 0x04) == 0 &&
                        other.DataLength == entry.DataLength &&
                        GetDicEntryAliases(other)
                            .Select(NormalizeDicRelativePath)
                            .Any(expected => JolietPathProjectsToIsoPath(relative, expected, masteringNamingProfile)) &&
                        (!timestampDisambiguated ||
                         (other.RecordingTime is DateTimeOffset otherRecordingTime &&
                          SourceTimestampMatchesDicRecordingTime(projected[0], otherRecordingTime))));

                    if (compatibleEntries == 1)
                    {
                        selected = projected[0];
                        matchMethod = timestampDisambiguated
                            ? "Joliet path -> DIC primary ISO9660 record + exact size + unique recording timestamp"
                            : "Joliet path -> DIC primary ISO9660 record + exact size";
                    }
                }
            }

            if (selected is not null && SourceAvailableForEntry(selected, entry))
            {
                relativePaths.TryGetValue(selected.FullName, out string? sourceRelativePath);
                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    selected.FullName,
                    string.Empty,
                    false,
                    matchMethod,
                    sourceRelativePath);
                ClaimSource(selected, entry);
            }

            processedEntries++;
            if (TryClaimProgressSlot(ref lastProgressTick, 100))
            {
                progress?.Report(new SkeletonSourceScanProgress(
                    processedEntries,
                    requiredEntries.Length,
                    processedEntries,
                    requiredEntries.Length,
                    entry.Path));
            }
        }

        // Some mastering tools resolve ISO9660 Level-1 filename collisions by replacing
        // the trailing underscore of the projected 8-character stem with a numeric
        // discriminator.  Street Fighter IV, for example, maps the Joliet names
        // Mar2009_d3dx10_41_x64.cab / ...x86.cab to MAR2009_.CAB / MAR20092.CAB.
        // The discriminator is collision-order dependent, so never predict its number.
        // Reconcile only the still-unmatched DIC records against source files not already
        // consumed by a stronger match, requiring complete parent-path compatibility,
        // collision-family compatibility, exact size, and bidirectional uniqueness.
        var usedSourcePaths = new HashSet<string>(
            matches.Values.Select(match => Path.GetFullPath(match.SourcePath)),
            GetPathComparer());
        SkeletonContentEntry[] collisionUnmatched = requiredEntries
            .Where(entry => !matches.ContainsKey(entry.Path))
            .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
            .ToArray();

        foreach (SkeletonContentEntry entry in collisionUnmatched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize))
                continue;

            FileInfo[] candidates = sameSize
                .Where(candidate => SourceAvailableForEntry(candidate, entry))
                .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                    GetDicEntryAliases(entry)
                                        .Select(NormalizeDicRelativePath)
                                        .Any(expected => JolietPathMatchesIsoCollisionAlias(relative, expected)))
                .GroupBy(candidate => candidate.FullName, GetPathComparer())
                .Select(group => group.First())
                .ToArray();

            if (candidates.Length != 1)
                continue;

            FileInfo selected = candidates[0];
            string selectedRelative = relativePaths[selected.FullName];
            int reverseCompatibleEntries = collisionUnmatched.Count(other =>
                !matches.ContainsKey(other.Path) &&
                (other.IsoFileFlags & 0x04) == 0 &&
                other.DataLength == selected.Length &&
                GetDicEntryAliases(other)
                    .Select(NormalizeDicRelativePath)
                    .Any(expected => JolietPathMatchesIsoCollisionAlias(selectedRelative, expected)));

            if (reverseCompatibleEntries != 1)
                continue;

            matches[entry.Path] = new SkeletonSourceMatch(
                entry,
                selected.FullName,
                string.Empty,
                false,
                "Joliet collision alias -> DIC primary ISO9660 record + exact unique size",
                selectedRelative);
            ClaimSource(selected, entry);
        }

        // Final conservative timestamp fallback for still-unmatched ordinary files.
        // Some Joliet names cannot be projected back to their ISO9660 8.3 alias at all
        // (for example language-specific/manual filenames whose generated ~N alias is
        // unrelated to the long name).  In that case, stay inside the same proven
        // parent directory, require exact byte size, then allow the existing ISO9660
        // recording timestamp to identify one unique remaining source file.  Timestamp
        // evidence is never used globally or without parent+size compatibility, and the
        // reverse mapping must also be unique so two DIC records cannot claim one source.
        usedSourcePaths = new HashSet<string>(
            matches.Values.Select(match => Path.GetFullPath(match.SourcePath)),
            GetPathComparer());
        SkeletonContentEntry[] timestampUnmatched = requiredEntries
            .Where(entry => !matches.ContainsKey(entry.Path))
            .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
            .Where(entry => entry.RecordingTime is not null)
            .ToArray();

        foreach (SkeletonContentEntry entry in timestampUnmatched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize) ||
                entry.RecordingTime is not DateTimeOffset expectedRecordingTime)
                continue;

            string[] expectedParents = GetDicEntryAliases(entry)
                .Select(NormalizeDicRelativePath)
                .Select(GetDicParentPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            FileInfo[] timestampFamilyCandidates = sameSize
                .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                    expectedParents.Any(expectedParent =>
                                        JolietParentProjectsToIsoParent(GetDicParentPath(relative), expectedParent)))
                .Where(candidate => SourceTimestampMatchesDicRecordingTime(candidate, expectedRecordingTime))
                .GroupBy(candidate => candidate.FullName, GetPathComparer())
                .Select(group => group.First())
                .ToArray();

            FileInfo[] candidates = timestampFamilyCandidates
                .Where(candidate => SourceAvailableForEntry(candidate, entry))
                .ToArray();

            if (candidates.Length != 1)
                continue;

            // v0.7.92: do not let prior source consumption turn an intrinsically ambiguous
            // ~N collision family into a false timestamp singleton. Wall Street Tycoon
            // demonstrated that one premature choice can cascade into several apparently
            // well-supported but wrong lexical-family choices. Keep such families unresolved
            // for the family-specific resolvers below unless timestamp+parent was unique before
            // any sibling was consumed.
            if (GetTildeAliasIndex(entry) is not null && timestampFamilyCandidates.Length > 1)
                continue;

            FileInfo selected = candidates[0];
            string selectedRelative = relativePaths[selected.FullName];
            string selectedParent = GetDicParentPath(selectedRelative);

            int reverseCompatibleEntries = timestampUnmatched.Count(other =>
                !matches.ContainsKey(other.Path) &&
                (other.IsoFileFlags & 0x04) == 0 &&
                other.DataLength == selected.Length &&
                other.RecordingTime is DateTimeOffset otherRecordingTime &&
                GetDicEntryAliases(other)
                    .Select(NormalizeDicRelativePath)
                    .Select(GetDicParentPath)
                    .Any(expectedParent => JolietParentProjectsToIsoParent(selectedParent, expectedParent)) &&
                SourceTimestampMatchesDicRecordingTime(selected, otherRecordingTime));

            if (reverseCompatibleEntries != 1)
                continue;

            matches[entry.Path] = new SkeletonSourceMatch(
                entry,
                selected.FullName,
                string.Empty,
                false,
                "Same-directory exact size + unique DIC recording timestamp",
                selectedRelative);
            ClaimSource(selected, entry);
        }

        // Final formatter-evidence fallback for unresolved same-size siblings.
        //
        // Some mastering tools allocate file payloads in case-sensitive ordinal Joliet
        // filename order even when their primary ISO9660 aliases are unrelated (~N names
        // or formatter-generated hashes). Do not assume that ordering globally. First
        // require at least three already-matched ordinary siblings in the same projected
        // parent directory whose source filenames are strictly increasing in ordinal
        // order as their DIC extents increase. Only then may a remaining same-size
        // ambiguous set be paired by that proven extent/name order. Counts must match,
        // every candidate must stay in the same parent, and no source may already be used.
        usedSourcePaths = new HashSet<string>(
            matches.Values.Select(match => Path.GetFullPath(match.SourcePath)),
            GetPathComparer());

        SkeletonContentEntry[] ordinalUnmatched = requiredEntries
            .Where(entry => !matches.ContainsKey(entry.Path))
            .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
            .ToArray();

        var unmatchedByParentAndSize = ordinalUnmatched
            .GroupBy(entry =>
            {
                string parent = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .Select(GetDicParentPath)
                    .FirstOrDefault() ?? string.Empty;
                return (Parent: parent.ToUpperInvariant(), entry.DataLength);
            });

        foreach (var group in unmatchedByParentAndSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SkeletonContentEntry[] unresolved = group
                .OrderBy(entry => entry.ExtentLba)
                .ToArray();
            if (unresolved.Length < 2)
                continue;

            string expectedParent = GetDicEntryAliases(unresolved[0])
                .Select(NormalizeDicRelativePath)
                .Select(GetDicParentPath)
                .FirstOrDefault() ?? string.Empty;

            // Restrict the ordinal proof to a formatter-generated numeric alias family
            // only when the *entire unresolved ambiguity set* belongs to that same
            // family.  A same-size set can legitimately contain unrelated short-name
            // families (Dead Man's Hand Disc 2 has DMCB77~1.PDF and DMH_MA~4.PDF).
            // Treating the first entry's family as authoritative for that mixed set
            // suppresses the older, independently proven parent-level ordinal fallback.
            // Unreal Gold's EXTREM~4/~6 groups, by contrast, all share one family and
            // continue to use the tighter family-local proof.
            // DOS 8.3 collision aliases shorten their textual prefix as the decimal
            // suffix grows: ANCHOR~9 becomes ANCHO~10, CURSOR~9 becomes CURSO~10,
            // etc.  Therefore the literal prefix is not itself a stable family key.
            // Seed the family from any numeric alias in the unresolved ambiguity set,
            // then require every member to be prefix-compatible with that family.  An
            // unsuffixed 8-character first member (for example Z5BUBBLE followed by
            // Z5BUB~1..~15) is also allowed when it shares the proven family prefix.
            bool hasNumericAliasFamily = false;
            string numericFamilyParent = string.Empty;
            string numericFamilyPrefix = string.Empty;
            string numericFamilyExtension = string.Empty;
            foreach (SkeletonContentEntry candidateEntry in unresolved)
            {
                if (!TryGetNumericShortAliasFamilyParts(candidateEntry, out numericFamilyParent, out numericFamilyPrefix, out numericFamilyExtension))
                    continue;
                hasNumericAliasFamily = true;
                break;
            }

            if (hasNumericAliasFamily && unresolved.Any(entry =>
                    !EntryBelongsToNumericShortAliasFamily(entry, numericFamilyParent, numericFamilyPrefix, numericFamilyExtension)))
            {
                hasNumericAliasFamily = false;
            }

            FileInfo[] sourceCandidates = bySize.TryGetValue(unresolved[0].DataLength, out List<FileInfo>? sameSize)
                ? sameSize
                    .Where(candidate => !sourceClaims.ContainsKey(Path.GetFullPath(candidate.FullName)))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        JolietParentProjectsToIsoParent(GetDicParentPath(relative), expectedParent))
                    // When the unresolved records belong to one proven ~N collision family,
                    // keep the ambiguity local to that family.  A common directory may contain
                    // unrelated files with the same tiny byte size; those must not prevent the
                    // already-proven sibling ordering rule from operating.
                    .Where(candidate => !hasNumericAliasFamily ||
                        (relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                         JolietSourceBelongsToNumericShortAliasFamily(
                             GetDicFilename(relative),
                             numericFamilyPrefix,
                             numericFamilyExtension)))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(candidateGroup => candidateGroup.First())
                    .OrderBy(candidate => GetDicFilename(relativePaths[candidate.FullName]), StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<FileInfo>();

            if (sourceCandidates.Length != unresolved.Length)
                continue;

            var anchors = matches.Values
                .Where(match => (match.Entry.IsoFileFlags & 0x04) == 0)
                .Where(match => relativePaths.TryGetValue(match.SourcePath, out string? relative) &&
                                JolietParentProjectsToIsoParent(GetDicParentPath(relative), expectedParent))
                // Prove ordering from siblings in the same formatter-generated numeric alias
                // family, not from every file in a large directory.  Unreal Gold, for example,
                // has EXTREM~1..~7 language files amongst hundreds of SYSTEM siblings; the five
                // already-unambiguous aliases prove the order of the remaining ~4/~6 pair.
                .Where(match => !hasNumericAliasFamily ||
                                EntryBelongsToNumericShortAliasFamily(
                                    match.Entry,
                                    numericFamilyParent,
                                    numericFamilyPrefix,
                                    numericFamilyExtension,
                                    allowPlainFirstMember: false))
                .Select(match => new
                {
                    match.Entry.ExtentLba,
                    SourceName = relativePaths.TryGetValue(match.SourcePath, out string? relative)
                        ? GetDicFilename(relative)
                        : string.Empty
                })
                .Where(anchor => anchor.SourceName.Length > 0)
                .OrderBy(anchor => anchor.ExtentLba)
                .ToArray();

            if (anchors.Length < 3)
                continue;

            bool ordinalOrderProven = true;
            for (int i = 1; i < anchors.Length; i++)
            {
                if (StringComparer.Ordinal.Compare(anchors[i - 1].SourceName, anchors[i].SourceName) >= 0)
                {
                    ordinalOrderProven = false;
                    break;
                }
            }

            if (!ordinalOrderProven)
                continue;

            for (int i = 0; i < unresolved.Length; i++)
            {
                SkeletonContentEntry entry = unresolved[i];
                FileInfo selected = sourceCandidates[i];
                string selectedRelative = relativePaths[selected.FullName];

                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    selected.FullName,
                    string.Empty,
                    false,
                    "Same-directory exact size + DIC-proven ordinal Joliet extent order",
                    selectedRelative);
                ClaimSource(selected, entry);
            }
        }

        // Final conservative resolver for duplicate-size holes inside an otherwise
        // independently proven local extent/name sequence.  Some ISO9660 generators use
        // one collision-number namespace across several long-name stems.  A record can
        // therefore jump from e.g. Z3LUR5~9 to Z3LU~105 even though the Joliet/source
        // names themselves remain locally ordered.  Do not try to infer that numbering
        // scheme.  Instead, when an unresolved entry is bracketed in physical extent
        // order by two already-proven source names in the same directory, accept a
        // same-size unused source only if exactly one candidate sorts strictly between
        // those two names.  Re-run until no more holes can be filled, because resolving
        // one duplicate-size hole can make another candidate unique/bracketed.
        bool bracketProgress;
        do
        {
            bracketProgress = false;
            usedSourcePaths = new HashSet<string>(
                matches.Values.Select(match => Path.GetFullPath(match.SourcePath)),
                GetPathComparer());

            SkeletonContentEntry[] bracketUnmatched = requiredEntries
                .Where(entry => !matches.ContainsKey(entry.Path))
                .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                .OrderBy(entry => entry.ExtentLba)
                .ToArray();

            foreach (SkeletonContentEntry entry in bracketUnmatched)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string entryParent = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .Select(GetDicParentPath)
                    .FirstOrDefault() ?? string.Empty;

                FileInfo[] candidates = bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize)
                    ? sameSize
                        .Where(candidate => SourceAvailableForEntry(candidate, entry))
                        .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                            JolietParentProjectsToIsoParent(GetDicParentPath(relative), entryParent))
                        .GroupBy(candidate => candidate.FullName, GetPathComparer())
                        .Select(g => g.First())
                        .ToArray()
                    : Array.Empty<FileInfo>();

                if (candidates.Length < 1)
                    continue;

                var localAnchors = matches.Values
                    .Where(match => (match.Entry.IsoFileFlags & 0x04) == 0)
                    .Where(match => relativePaths.TryGetValue(match.SourcePath, out string? relative) &&
                                    JolietParentProjectsToIsoParent(GetDicParentPath(relative), entryParent))
                    .Select(match => new
                    {
                        match.Entry.ExtentLba,
                        SourceName = relativePaths.TryGetValue(match.SourcePath, out string? relative)
                            ? GetDicFilename(relative)
                            : string.Empty
                    })
                    .Where(anchor => anchor.SourceName.Length > 0)
                    .OrderBy(anchor => anchor.ExtentLba)
                    .ToArray();

                var before = localAnchors.LastOrDefault(anchor => anchor.ExtentLba < entry.ExtentLba);
                var after = localAnchors.FirstOrDefault(anchor => anchor.ExtentLba > entry.ExtentLba);
                if (before is null || after is null)
                    continue;

                // The two proven neighbors must themselves establish an increasing local
                // filename interval.  This deliberately avoids using a directory-wide
                // ordering assumption, which is not valid for every mastering program.
                if (StringComparer.Ordinal.Compare(before.SourceName, after.SourceName) >= 0)
                    continue;

                FileInfo[] bracketed = candidates
                    .Where(candidate =>
                    {
                        string name = GetDicFilename(relativePaths[candidate.FullName]);
                        return StringComparer.Ordinal.Compare(before.SourceName, name) < 0 &&
                               StringComparer.Ordinal.Compare(name, after.SourceName) < 0;
                    })
                    .ToArray();

                if (bracketed.Length != 1)
                    continue;

                FileInfo selected = bracketed[0];
                string selectedRelative = relativePaths[selected.FullName];
                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    selected.FullName,
                    string.Empty,
                    false,
                    "Same-directory exact size + DIC-proven local extent/name bracket",
                    selectedRelative);
                ClaimSource(selected, entry);
                bracketProgress = true;
            }
        }
        while (bracketProgress);

        // Once two or more extensions have independently proved a DOS-style numeric
        // alias identity (for example EXTREM~4.DET/EST -> ExtremeDGen.det/est), carry
        // that proven ~N -> long-stem identity across sibling extensions.  This avoids
        // forcing same-sized tiny files such as .FRT/.ITT to rediscover an identity
        // that the formatter family has already established.  The propagation remains
        // conservative: same parent, same short prefix and numeric suffix, at least two
        // independently matched extension anchors agreeing on one long stem, exact size,
        // matching extension, and a unique unused source candidate are all required.
        static bool TryGetNumericAliasIdentity(SkeletonContentEntry entry, out string parent, out string prefix, out int number, out string extension)
        {
            foreach (string alias in GetDicEntryAliases(entry))
            {
                string normalized = NormalizeDicRelativePath(alias);
                string file = GetDicFilename(normalized);
                int dot = file.LastIndexOf('.');
                string stem = dot > 0 ? file[..dot] : file;
                extension = dot > 0 ? file[(dot + 1)..].ToUpperInvariant() : string.Empty;
                Match m = Regex.Match(stem, @"^(?<prefix>[A-Z0-9_]{1,6})(?:~|_)(?<num>[1-9][0-9]*)$", RegexOptions.IgnoreCase);
                if (!m.Success || !int.TryParse(m.Groups["num"].Value, out number))
                    continue;

                parent = GetDicParentPath(normalized).ToUpperInvariant();
                prefix = m.Groups["prefix"].Value.ToUpperInvariant();
                return true;
            }

            parent = string.Empty;
            prefix = string.Empty;
            number = 0;
            extension = string.Empty;
            return false;
        }

        static string GetFilenameStem(string file)
        {
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        var numericIdentityAnchors = matches.Values
            .Select(match =>
            {
                if (!TryGetNumericAliasIdentity(match.Entry, out string parent, out string prefix, out int number, out string extension) ||
                    !relativePaths.TryGetValue(match.SourcePath, out string? relative))
                    return null;

                string sourceFile = GetDicFilename(relative);
                int sourceDot = sourceFile.LastIndexOf('.');
                string sourceExtension = sourceDot > 0 ? sourceFile[(sourceDot + 1)..].ToUpperInvariant() : string.Empty;
                if (!string.Equals(sourceExtension, extension, StringComparison.OrdinalIgnoreCase))
                    return null;

                return new
                {
                    Key = parent + "|" + prefix + "|" + number.ToString(CultureInfo.InvariantCulture),
                    Extension = extension,
                    LongStem = GetFilenameStem(sourceFile)
                };
            })
            .Where(anchor => anchor is not null)
            .Select(anchor => anchor!)
            .GroupBy(anchor => anchor.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                group.Key,
                DistinctExtensions = group.Select(x => x.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                LongStems = group.Select(x => x.LongStem).Distinct(StringComparer.Ordinal).ToArray()
            })
            .Where(group => group.DistinctExtensions >= 2 && group.LongStems.Length == 1)
            .ToDictionary(group => group.Key, group => group.LongStems[0], StringComparer.OrdinalIgnoreCase);

        foreach (SkeletonContentEntry entry in requiredEntries.Where(entry => !matches.ContainsKey(entry.Path)).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetNumericAliasIdentity(entry, out string parent, out string prefix, out int number, out string extension))
                continue;

            string key = parent + "|" + prefix + "|" + number.ToString(CultureInfo.InvariantCulture);
            if (!numericIdentityAnchors.TryGetValue(key, out string? provenLongStem))
                continue;

            FileInfo[] candidates = bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize)
                ? sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        JolietParentProjectsToIsoParent(GetDicParentPath(relative), parent))
                    .Where(candidate =>
                    {
                        string sourceFile = GetDicFilename(relativePaths[candidate.FullName]);
                        int dot = sourceFile.LastIndexOf('.');
                        string sourceExtension = dot > 0 ? sourceFile[(dot + 1)..] : string.Empty;
                        return string.Equals(sourceExtension, extension, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(GetFilenameStem(sourceFile), provenLongStem, StringComparison.Ordinal);
                    })
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray()
                : Array.Empty<FileInfo>();

            if (candidates.Length != 1)
                continue;

            FileInfo selected = candidates[0];
            string selectedRelative = relativePaths[selected.FullName];
            matches[entry.Path] = new SkeletonSourceMatch(
                entry,
                selected.FullName,
                string.Empty,
                false,
                "Same-directory exact size + proven numeric alias identity across sibling extensions",
                selectedRelative);
            ClaimSource(selected, entry);
        }

        // v0.7.92: strict zero-based terminal ordinal families. Some mastering runs use
        // DOS collision aliases ~1, ~2, ... for long names whose own visible terminal number
        // is 0, 1, ... (for example HIGHLI~1.BMP -> highlight0.bmp and HIGHLI~2.BMP ->
        // highlight1.bmp). This is accepted only as a CLOSED local family: at least two
        // unresolved aliases in one parent/family/extension, consecutive alias indices starting
        // at 1, exactly the same number of unused family-compatible source files, and consecutive
        // source terminal numbers starting at 0. Exact size and recording timestamp must also
        // agree for every pair. This avoids treating a lone foo~1 -> foo0 resemblance as proof.
        var zeroBasedGroups = requiredEntries
            .Where(entry => !matches.ContainsKey(entry.Path))
            .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
            .Select(entry => TryGetTildeAliasInfo(entry, out string parent, out string family, out int index, out string extension)
                ? new { Entry = entry, Parent = parent, Family = family, Index = index, Extension = extension }
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Parent + "|" + item.Family + "|" + item.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var familyGroup in zeroBasedGroups)
        {
            var targets = familyGroup.OrderBy(item => item.Index).ToArray();
            if (targets.Length < 2)
                continue;
            if (!targets.Select((item, position) => item.Index == position + 1).All(value => value))
                continue;

            string targetParent = targets[0].Parent;
            string familyKey = targets[0].Family;
            string targetExtension = targets[0].Extension;
            var familySources = files
                .Where(candidate => !sourceClaims.ContainsKey(Path.GetFullPath(candidate.FullName)))
                .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                    JolietParentProjectsToIsoParent(GetDicParentPath(relative), targetParent) &&
                                    SourceMatchesAliasFamily(GetDicFilename(relative), familyKey, targetExtension) &&
                                    TryGetTerminalNumber(GetDicFilename(relative), out _))
                .GroupBy(candidate => candidate.FullName, GetPathComparer())
                .Select(group => group.First())
                .Select(candidate => new
                {
                    File = candidate,
                    Number = TryGetTerminalNumber(GetDicFilename(relativePaths[candidate.FullName]), out int number) ? number : -1
                })
                .OrderBy(item => item.Number)
                .ToArray();

            if (familySources.Length != targets.Length ||
                !familySources.Select((item, position) => item.Number == position).All(value => value))
                continue;

            bool allPairsCompatible = true;
            for (int i = 0; i < targets.Length; i++)
            {
                SkeletonContentEntry target = targets[i].Entry;
                FileInfo source = familySources[i].File;
                if (source.Length != target.DataLength ||
                    (target.RecordingTime is DateTimeOffset timestamp && !SourceTimestampMatchesDicRecordingTime(source, timestamp)) ||
                    !SourceAvailableForEntry(source, target))
                {
                    allPairsCompatible = false;
                    break;
                }
            }
            if (!allPairsCompatible)
                continue;

            for (int i = 0; i < targets.Length; i++)
            {
                SkeletonContentEntry target = targets[i].Entry;
                FileInfo source = familySources[i].File;
                matches[target.Path] = new SkeletonSourceMatch(target, source.FullName, string.Empty, false,
                    "DICSimulator ZERO_BASED_TERMINAL_ORDINAL_FAMILY", relativePaths[source.FullName]);
                ClaimSource(source, target);
            }
        }

        // v0.3.5: port the DICSimulator rules that remained clean across the A/A2/A3/A4
        // holdout corpus. These are deliberately local, evidence-learned rules. They do not
        // assume a global ~N ordering and they never use target/oracle payload hashes.
        //
        // Run them to a fixpoint because a newly proved family member can become the second
        // anchor required to prove another member of the same local collision family.
        bool simulatorRuleProgress;
        do
        {
            simulatorRuleProgress = false;

            // Windows NT hashed 8.3 names (RtlGenerate8dot3Name checksum form) are deterministic
            // from the long filename. Require exact length, compatible parent, recording timestamp
            // where DIC has one, one unused candidate, and the exact checksum-form alias.
            foreach (SkeletonContentEntry entry in requiredEntries
                         .Where(entry => !matches.ContainsKey(entry.Path))
                         .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] targetAliases = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .ToArray();
                string[] hashedTargetLeaves = targetAliases
                    .Select(GetDicFilename)
                    .Select(StripIsoVersionForMatching)
                    .Where(leaf => Regex.IsMatch(leaf, @"^[A-Z0-9_]{2}[0-9A-F]{4}~1(?:\.[A-Z0-9_]{1,3})?$", RegexOptions.IgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (hashedTargetLeaves.Length == 0 || !bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize))
                    continue;

                string[] targetParents = targetAliases.Select(GetDicParentPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                FileInfo[] candidates = sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        targetParents.Any(parent => JolietParentProjectsToIsoParent(GetDicParentPath(relative), parent)))
                    .Where(candidate => entry.RecordingTime is not DateTimeOffset timestamp ||
                                        SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        hashedTargetLeaves.Any(target => WindowsHashed83Leaf(GetDicFilename(relative))
                                            .Equals(target, StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                if (candidates.Length != 1)
                    continue;

                FileInfo selected = candidates[0];
                string selectedRelative = relativePaths[selected.FullName];
                matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                    "DICSimulator WINDOWS_NT_HASHED_83_EXACT", selectedRelative);
                ClaimSource(selected, entry);
                simulatorRuleProgress = true;
            }

            // v0.7.98: deterministic Windows hashed-8.3 path-chain resolver.  The existing
            // WINDOWS_NT_HASHED_83_EXACT rule requires a previously-proven parent map; that is too
            // restrictive when parent directories themselves were generated with Windows checksum
            // aliases.  Here every path component is compared deterministically: an ordinary
            // ISO9660 projection or the exact RtlGenerate8dot3Name checksum-form alias may satisfy
            // the corresponding target component.  Exact size/time and one unused source remain
            // mandatory, so this does not turn into a fuzzy path guess.
            foreach (SkeletonContentEntry entry in requiredEntries
                         .Where(entry => !matches.ContainsKey(entry.Path))
                         .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] targetAliases = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize))
                    continue;

                FileInfo[] candidates = sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => entry.RecordingTime is not DateTimeOffset timestamp ||
                                        SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        targetAliases.Any(target => WindowsHashed83PathProjectsToTarget(relative, target)))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                if (candidates.Length != 1)
                    continue;

                FileInfo selected = candidates[0];
                matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                    "DICSimulator WINDOWS_NT_HASHED_83_PATH_CHAIN_EXACT", relativePaths[selected.FullName]);
                ClaimSource(selected, entry);
                simulatorRuleProgress = true;
            }

            // v0.7.98: Acclaim/Xbox-style PREFIX3_HEX_ORDINAL path-chain resolver.  The formatter
            // derives PREFIX_XXXX from the first three folded characters and the one-based ordinal
            // among *all* siblings in the source directory.  Permit this deterministic transform at
            // any path level (directory and leaf) while also allowing components which stayed as an
            // ordinary ISO projection.  This handles press-kit trees where both parent directory and
            // child filenames use the ordinal scheme.  No oracle data, payload hashes or lexical
            // ranking participate; exact size/time and uniqueness are still required.
            foreach (SkeletonContentEntry entry in requiredEntries
                         .Where(entry => !matches.ContainsKey(entry.Path))
                         .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize))
                    continue;
                string[] targetAliases = GetDicEntryAliases(entry)
                    .Select(NormalizeDicRelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                FileInfo[] candidates = sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => entry.RecordingTime is not DateTimeOffset timestamp ||
                                        SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        targetAliases.Any(target => Prefix3HexOrdinalPathProjectsToTarget(directory, relative, target)))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                if (candidates.Length != 1)
                    continue;

                FileInfo selected = candidates[0];
                matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                    "DICSimulator PREFIX3_HEX_ORDINAL_PATH_CHAIN", relativePaths[selected.FullName]);
                ClaimSource(selected, entry);
                simulatorRuleProgress = true;
            }

            // Learn a local affine relationship between a primary ~N index and a terminal
            // decimal number in the long source filename. At least two already-proven members
            // of the same parent+extension+collision family must independently agree on one
            // constant delta. The predicted member must then be unique by parent/size/time.
            var unresolvedAliasEntries = requiredEntries
                .Where(entry => !matches.ContainsKey(entry.Path))
                .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                .Where(entry => TryGetTildeAliasInfo(entry, out _, out _, out _, out _))
                .ToArray();

            foreach (SkeletonContentEntry unresolved in unresolvedAliasEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetTildeAliasInfo(unresolved, out string targetParent, out string familyKey,
                        out int targetAliasIndex, out string targetExtension))
                    continue;

                var anchors = matches.Values
                    .Where(match => TryGetTildeAliasInfo(match.Entry, out string anchorParent, out string anchorFamily,
                                      out int anchorAliasIndex, out string anchorExtension) &&
                                    anchorParent.Equals(targetParent, StringComparison.OrdinalIgnoreCase) &&
                                    anchorFamily.Equals(familyKey, StringComparison.OrdinalIgnoreCase) &&
                                    anchorExtension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(match.SourceRelativePath))
                    .Select(match => new
                    {
                        Match = match,
                        AliasIndex = GetTildeAliasIndex(match.Entry),
                        Terminal = TryGetTerminalNumber(GetDicFilename(match.SourceRelativePath!), out int terminal) ? terminal : (int?)null
                    })
                    .Where(anchor => anchor.AliasIndex is not null && anchor.Terminal is not null)
                    .ToArray();

                int[] deltas = anchors.Select(anchor => anchor.AliasIndex!.Value - anchor.Terminal!.Value).ToArray();
                if (deltas.Length >= 2 && deltas.Distinct().Count() == 1)
                {
                    int wantedTerminal = targetAliasIndex - deltas[0];
                    if (bySize.TryGetValue(unresolved.DataLength, out List<FileInfo>? sameSize))
                    {
                        FileInfo[] candidates = sameSize
                            .Where(candidate => SourceAvailableForEntry(candidate, unresolved))
                            .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                                JolietParentProjectsToIsoParent(GetDicParentPath(relative), targetParent))
                            .Where(candidate => unresolved.RecordingTime is not DateTimeOffset timestamp ||
                                                SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
                            .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                                SourceMatchesAliasFamily(GetDicFilename(relative), familyKey, targetExtension) &&
                                                TryGetTerminalNumber(GetDicFilename(relative), out int terminal) && terminal == wantedTerminal)
                            .GroupBy(candidate => candidate.FullName, GetPathComparer())
                            .Select(group => group.First())
                            .ToArray();
                        if (candidates.Length == 1)
                        {
                            FileInfo selected = candidates[0];
                            matches[unresolved.Path] = new SkeletonSourceMatch(unresolved, selected.FullName, string.Empty, false,
                                "DICSimulator AFFINE_ALIAS_INDEX_FROM_PROVEN_FAMILY", relativePaths[selected.FullName]);
                            ClaimSource(selected, unresolved);
                            simulatorRuleProgress = true;
                            continue;
                        }
                    }
                }

                // Alternative locally learned rule: rank compatible source long names lexically and
                // learn one constant (~N - lexical rank) from >=2 proven siblings. Every anchor must
                // agree. This is intentionally local to one collision family and mapped parent.
                FileInfo[] familySources = files
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        JolietParentProjectsToIsoParent(GetDicParentPath(relative), targetParent) &&
                                        SourceMatchesAliasFamily(GetDicFilename(relative), familyKey, targetExtension))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .OrderBy(candidate => GetDicFilename(relativePaths[candidate.FullName]), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => GetDicFilename(relativePaths[candidate.FullName]), StringComparer.Ordinal)
                    .ToArray();
                if (familySources.Length < 2)
                    continue;

                var rankDeltas = new List<int>();
                foreach (var anchor in matches.Values)
                {
                    if (!TryGetTildeAliasInfo(anchor.Entry, out string anchorParent, out string anchorFamily,
                            out int anchorAliasIndex, out string anchorExtension) ||
                        !anchorParent.Equals(targetParent, StringComparison.OrdinalIgnoreCase) ||
                        !anchorFamily.Equals(familyKey, StringComparison.OrdinalIgnoreCase) ||
                        !anchorExtension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int sourceRank = Array.FindIndex(familySources, candidate =>
                        GetPathComparer().Equals(Path.GetFullPath(candidate.FullName), Path.GetFullPath(anchor.SourcePath)));
                    if (sourceRank >= 0)
                        rankDeltas.Add(anchorAliasIndex - (sourceRank + 1));
                }
                if (rankDeltas.Count < 2 || rankDeltas.Distinct().Count() != 1)
                    continue;

                // v0.7.92: interpolation only, never extrapolation. Two anchors that both
                // lie before (or both after) an unresolved member do not prove lexical ordering
                // beyond the observed interval. This blocks the Wall Street Tycoon cascade while
                // preserving bracketed holes in otherwise proven local families.
                int[] anchorAliasIndices = matches.Values
                    .Where(anchor => TryGetTildeAliasInfo(anchor.Entry, out string anchorParent, out string anchorFamily,
                                      out int anchorAliasIndex, out string anchorExtension) &&
                                    anchorParent.Equals(targetParent, StringComparison.OrdinalIgnoreCase) &&
                                    anchorFamily.Equals(familyKey, StringComparison.OrdinalIgnoreCase) &&
                                    anchorExtension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(anchor.SourceRelativePath))
                    .Select(anchor => GetTildeAliasIndex(anchor.Entry))
                    .Where(index => index is not null)
                    .Select(index => index!.Value)
                    .Distinct()
                    .ToArray();
                if (!anchorAliasIndices.Any(index => index < targetAliasIndex) ||
                    !anchorAliasIndices.Any(index => index > targetAliasIndex))
                    continue;

                int predictedRank = targetAliasIndex - rankDeltas[0];
                if (predictedRank < 1 || predictedRank > familySources.Length)
                    continue;
                FileInfo predicted = familySources[predictedRank - 1];
                if (predicted.Length != unresolved.DataLength || !SourceAvailableForEntry(predicted, unresolved) ||
                    (unresolved.RecordingTime is DateTimeOffset expectedTime && !SourceTimestampMatchesDicRecordingTime(predicted, expectedTime)))
                    continue;

                matches[unresolved.Path] = new SkeletonSourceMatch(unresolved, predicted.FullName, string.Empty, false,
                    "DICSimulator LEXICAL_ALIAS_RANK_FROM_PROVEN_FAMILY", relativePaths[predicted.FullName]);
                ClaimSource(predicted, unresolved);
                simulatorRuleProgress = true;
            }

            // v0.8.9: once stronger mappings have proved that this disc actually uses the
            // Windows NT checksum-form 8.3 namespace, an exact RtlGenerate8dot3Name leaf is
            // strong enough to cross an unresolved parent alias.  This is deliberately a
            // disc-profile rule: without an earlier proven hashed-8.3 mapping it does not run.
            // Exact size, compatible DIC timestamp, source availability and one surviving
            // candidate are still mandatory.
            bool provenWindows83Profile = matches.Values.Any(match =>
                match.MatchMethod.Equals("DICSimulator WINDOWS_NT_HASHED_83_EXACT", StringComparison.OrdinalIgnoreCase) ||
                match.MatchMethod.Equals("DICSimulator WINDOWS_NT_HASHED_83_PATH_CHAIN_EXACT", StringComparison.OrdinalIgnoreCase) ||
                match.MatchMethod.Equals("DICSimulator WINDOWS_NT_HASHED_83_FROM_PROVEN_DISC_PROFILE", StringComparison.OrdinalIgnoreCase));

            if (provenWindows83Profile)
            {
                foreach (SkeletonContentEntry entry in requiredEntries
                             .Where(entry => !matches.ContainsKey(entry.Path))
                             .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                             .ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize))
                        continue;

                    string[] hashedLeaves = GetDicEntryAliases(entry)
                        .Select(NormalizeDicRelativePath)
                        .Select(GetDicFilename)
                        .Select(StripIsoVersionForMatching)
                        .Where(leaf => Regex.IsMatch(leaf, @"^[A-Z0-9_]{2}[0-9A-F]{4}~1(?:\.[A-Z0-9_]{1,3})?$", RegexOptions.IgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (hashedLeaves.Length == 0)
                        continue;

                    FileInfo[] candidates = sameSize
                        .Where(candidate => SourceAvailableForEntry(candidate, entry))
                        .Where(candidate => entry.RecordingTime is not DateTimeOffset timestamp ||
                                            SourceTimestampMatchesDicRecordingTime(candidate, timestamp))
                        .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                            hashedLeaves.Any(target => WindowsHashed83Leaf(GetDicFilename(relative))
                                                .Equals(target, StringComparison.OrdinalIgnoreCase)))
                        .GroupBy(candidate => candidate.FullName, GetPathComparer())
                        .Select(group => group.First())
                        .ToArray();
                    if (candidates.Length != 1)
                        continue;

                    FileInfo selected = candidates[0];
                    matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                        "DICSimulator WINDOWS_NT_HASHED_83_FROM_PROVEN_DISC_PROFILE", relativePaths[selected.FullName]);
                    ClaimSource(selected, entry);
                    simulatorRuleProgress = true;
                }
            }

            // v0.8.9: a proved child mapping also proves its parent-directory correspondence.
            // Rebuild that parent map after every progress pass and retry unresolved children
            // inside mutual-unique parent pairs.  This is the important fixpoint behaviour: a
            // newly proven folder can immediately unlock siblings that were previously blocked
            // by an opaque ISO9660 directory alias.
            Dictionary<string, string> provenParents = BuildProvenParentMap();
            foreach (SkeletonContentEntry entry in requiredEntries
                         .Where(entry => !matches.ContainsKey(entry.Path))
                         .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                         .Where(entry => entry.RecordingTime is not null)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string targetParent = GetPrimaryIsoParent(entry);
                if (!provenParents.TryGetValue(targetParent, out string? sourceParent) ||
                    !bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize) ||
                    entry.RecordingTime is not DateTimeOffset expectedTime)
                    continue;

                FileInfo[] parentCandidates = sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        GetDicParentPath(relative).Equals(sourceParent, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => SourceTimestampMatchesDicRecordingTime(candidate, expectedTime))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                if (parentCandidates.Length == 1)
                {
                    FileInfo selected = parentCandidates[0];
                    matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                        "DICSimulator PROVEN_PARENT_RESCAN_SIZE_TIMESTAMP", relativePaths[selected.FullName]);
                    ClaimSource(selected, entry);
                    simulatorRuleProgress = true;
                    continue;
                }

                // If size+timestamp still leaves siblings, apply only the narrow local ~N
                // family-prefix relation already used by the conservative alias matcher.  It
                // may break a tie, but it may not infer lexical ~N ordering.
                if (!TryGetTildeAliasInfo(entry, out _, out string familyKey, out _, out string extension))
                    continue;
                FileInfo[] aliasCandidates = parentCandidates
                    .Where(candidate => relativePaths.TryGetValue(candidate.FullName, out string? relative) &&
                                        SourceMatchesAliasFamily(GetDicFilename(relative), familyKey, extension))
                    .ToArray();
                if (aliasCandidates.Length != 1)
                    continue;

                FileInfo aliasSelected = aliasCandidates[0];
                matches[entry.Path] = new SkeletonSourceMatch(entry, aliasSelected.FullName, string.Empty, false,
                    "DICSimulator PROVEN_PARENT_STRICT_ALIAS_SIZE_TIMESTAMP", relativePaths[aliasSelected.FullName]);
                ClaimSource(aliasSelected, entry);
                simulatorRuleProgress = true;
            }

            // Last-resort residual elimination.  After every stronger rule has consumed what it
            // can, permit size+timestamp alone only when the remaining relation is mutual-unique:
            // this target has one eligible unused source and that source fits one unresolved
            // target.  No filename, directory order or oracle information participates.
            SkeletonContentEntry[] residualTargets = requiredEntries
                .Where(entry => !matches.ContainsKey(entry.Path))
                .Where(entry => (entry.IsoFileFlags & 0x04) == 0)
                .Where(entry => entry.RecordingTime is not null)
                .ToArray();
            foreach (SkeletonContentEntry entry in residualTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bySize.TryGetValue(entry.DataLength, out List<FileInfo>? sameSize) ||
                    entry.RecordingTime is not DateTimeOffset expectedTime)
                    continue;

                FileInfo[] candidates = sameSize
                    .Where(candidate => SourceAvailableForEntry(candidate, entry))
                    .Where(candidate => SourceTimestampMatchesDicRecordingTime(candidate, expectedTime))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();
                if (candidates.Length != 1)
                    continue;

                FileInfo selected = candidates[0];
                int reverseCompatibleTargets = residualTargets.Count(other =>
                    !matches.ContainsKey(other.Path) &&
                    other.DataLength == selected.Length &&
                    other.RecordingTime is DateTimeOffset otherTime &&
                    SourceAvailableForEntry(selected, other) &&
                    SourceTimestampMatchesDicRecordingTime(selected, otherTime));
                if (reverseCompatibleTargets != 1)
                    continue;

                matches[entry.Path] = new SkeletonSourceMatch(entry, selected.FullName, string.Empty, false,
                    "DICSimulator RESIDUAL_MUTUAL_UNIQUE_SIZE_TIMESTAMP", relativePaths[selected.FullName]);
                ClaimSource(selected, entry);
                simulatorRuleProgress = true;
            }
        }
        while (simulatorRuleProgress);

        // Zero-length ordinary files require no payload, so they are intentionally
        // absent from requiredEntries.  They may still physically exist in an extracted
        // source tree, however, and that pathname is useful evidence when reconstructing
        // Joliet.  Record a unique exact path+size match without hashing/copying data.
        // This match is identity-only and must never become a queued payload.
        SkeletonContentEntry[] zeroLengthEntries = inspection.Entries
            .Where(e => e.CanRestore && e.IsEmpty && e.DataLength == 0)
            .Where(e => e.SpecialKind == SkeletonSpecialKind.None)
            .Where(e => (e.IsoFileFlags & 0x04) == 0)
            .ToArray();

        foreach (SkeletonContentEntry entry in zeroLengthEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo? selected = null;

            foreach (string alias in GetDicEntryAliases(entry))
            {
                string expectedPath = NormalizeDicRelativePath(alias);
                string expectedName = GetDicFilename(expectedPath);
                string key = DicPathSizeKey(expectedPath, 0);
                if (!bySizeAndPath.TryGetValue(key, out List<FileInfo>? candidates))
                    continue;

                FileInfo[] exact = candidates
                    .Where(candidate => candidate.Length == 0)
                    .Where(candidate => candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(candidate => candidate.FullName, GetPathComparer())
                    .Select(group => group.First())
                    .ToArray();

                if (exact.Length == 1)
                {
                    selected = exact[0];
                    break;
                }
            }

            if (selected is not null)
            {
                relativePaths.TryGetValue(selected.FullName, out string? sourceRelativePath);
                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    selected.FullName,
                    string.Empty,
                    false,
                    "Joliet/ISO9660 exact zero-length source path (no payload required)",
                    sourceRelativePath);
            }
        }

        // If at least one entry required the Joliet->ISO projection fallback, the
        // selected folder is demonstrably a Joliet/user-visible tree. Exact primary-
        // compatible names from the same scan are therefore also valid Joliet name
        // evidence (many files have identical names in both namespaces).
        bool scanIsJolietTree = matches.Values.Any(match =>
            match.MatchMethod.StartsWith("Joliet ", StringComparison.OrdinalIgnoreCase));
        if (scanIsJolietTree)
        {
            foreach (string key in matches.Keys.ToArray())
            {
                SkeletonSourceMatch match = matches[key];
                if (!string.IsNullOrWhiteSpace(match.SourceRelativePath) &&
                    match.MatchMethod.Equals("ISO9660 exact relative path+filename+size", StringComparison.OrdinalIgnoreCase))
                {
                    matches[key] = match with
                    {
                        MatchMethod = "Joliet tree path (primary-compatible spelling) + exact size"
                    };
                }
            }
        }

        // A mounted optical disc may expose only its primary ISO9660 namespace through
        // normal filesystem enumeration even though the disc also contains Joliet. Read
        // the supplementary tree directly from the read-only volume and attach only
        // one-to-one extent/length mappings to source matches already proven above.
        // This changes pathname evidence only; payload identity and placement remain
        // governed by the existing DIC matching rules.
        IReadOnlyDictionary<string, string> mountedDiscJolietPaths =
            MountedDiscJolietPathService.TryRead(directory, cancellationToken);
        MountedDiscJolietPathService.EnrichMatches(matches, mountedDiscJolietPaths);

        progress?.Report(new SkeletonSourceScanProgress(
            requiredEntries.Length,
            requiredEntries.Length,
            requiredEntries.Length,
            requiredEntries.Length,
            string.Empty));

        return matches;
    }

    private static void AddCandidate(Dictionary<string, List<FileInfo>> dictionary, string key, FileInfo file)
    {
        if (!dictionary.TryGetValue(key, out List<FileInfo>? values))
            dictionary[key] = values = new List<FileInfo>();
        values.Add(file);
    }

    private static bool TryClaimProgressSlot(ref long lastTick, int minimumIntervalMilliseconds)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref lastTick);
        if (now - previous < minimumIntervalMilliseconds)
            return false;

        return Interlocked.CompareExchange(ref lastTick, now, previous) == previous;
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}
