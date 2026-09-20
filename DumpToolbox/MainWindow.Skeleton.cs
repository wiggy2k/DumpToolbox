using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow : Window
{
    private async void SkeletonBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose redumper skeleton",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Skeletons and compressed skeletons")
                {
                    Patterns = new[] { "*.skeleton", "*.zst", "*.zstd", "*.7z", "*.zip", "*.rar", "*.gz", "*.xz", "*.bz2" }
                },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        SkeletonPathBox.Text = path;
        string[] hashCandidates =
        [
            Path.ChangeExtension(path, ".hash"),
            Path.ChangeExtension(Path.ChangeExtension(path, null), ".hash")
        ];
        string? hash = hashCandidates.FirstOrDefault(File.Exists);
        if (hash is not null)
            SkeletonHashBox.Text = hash;
    }

    private async void SkeletonHashBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose redumper hash manifest",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            SkeletonHashBox.Text = path;
    }

    private async void SkeletonSourceBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder containing source files",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            SkeletonSourceFolderBox.Text = path;
    }

    private async void SkeletonSourceImageBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose source disc image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Disc images") { Patterns = new[] { "*.iso", "*.bin", "*.img" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            SkeletonSourceImageBox.Text = path;
    }

    private async void SkeletonOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = _skeletonInspection is null
            ? "resurrected.bin"
            : Path.GetFileName(_skeletonService.SuggestOutputPath(_skeletonInspection));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose resurrected output image",
            SuggestedFileName = suggested
        });

        if (file?.TryGetLocalPath() is { } path)
            SkeletonOutputBox.Text = path;
    }

    private async void SkeletonLoadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_skeletonCts is not null)
            return;

        try
        {
            string skeleton = SkeletonPathBox.Text?.Trim() ?? string.Empty;
            string hash = SkeletonHashBox.Text?.Trim() ?? string.Empty;

            SkeletonLogPanel.Children.Clear();
            _skeletonActivityLogText.Clear();
            UpdateSkeletonDetachedLog();
            _skeletonTreeRoots.Clear();
            _skeletonNodes.Clear();
            _skeletonMatches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
            _skeletonInspection = null;
            SkeletonProgressBar.Value = 0;
            SkeletonProgressText.Text = "Loading...";
            AppendSkeletonLog($"Loading skeleton: {skeleton}");
            AppendSkeletonLog($"Hash manifest: {hash}");

            _skeletonCts = new CancellationTokenSource();
            SetSkeletonRunning(true);

            var preparationProgress = new Progress<SkeletonInputPreparationProgress>(p =>
            {
                SkeletonProgressBar.IsIndeterminate = p.Fraction is null;
                if (p.Fraction is { } fraction)
                    SkeletonProgressBar.Value = fraction * 100;
                SkeletonProgressText.Text = p.Message;
            });

            SkeletonInspectionResult inspection = await _skeletonService.InspectAsync(
                skeleton,
                hash,
                preparationProgress,
                _skeletonCts.Token);

            SkeletonProgressBar.IsIndeterminate = false;
            if (inspection.SkeletonWasCompressed)
            {
                AppendSkeletonLog(
                    $"Prepared {inspection.SkeletonInputFormat}; expanded to a temporary random-access working copy " +
                    $"({new FileInfo(inspection.EffectiveSkeletonPath).Length:N0} bytes). The selected file was not changed. " +
                    $"Working copy: {inspection.EffectiveSkeletonPath}");
            }

            _skeletonInspection = inspection;
            BuildSkeletonTree(inspection);
            SkeletonOutputBox.Text = _skeletonService.SuggestOutputPath(inspection);

            if (IsSha1DatabaseEnabled)
            {
                SkeletonProgressText.Text = "Checking SHA-1 catalogue...";
                IReadOnlyDictionary<string, SkeletonSourceMatch> catalogueMatches =
                    await _skeletoolCatalogueService.FindMatchesAsync(inspection, _skeletonCts.Token);
                _skeletonMatches = MergeSkeletonMatches(_skeletonMatches, catalogueMatches);
                MarkSkeletonMissingStatuses();
                if (catalogueMatches.Count > 0)
                    AppendSkeletonLog($"SHA-1 catalogue supplied {catalogueMatches.Count:N0} matching payload(s). Local SkeleTool sources will take priority if supplied.");
            }

            int normalFiles = inspection.Entries.Count(e => e.SpecialKind == SkeletonSpecialKind.None);
            int special = inspection.Entries.Count(e => e.IsSpecial);
            string kind = inspection.ImageKind == SkeletonImageKind.Raw2352
                ? $"raw 2352-byte CD data track, base LBA {inspection.BaseLba:N0}"
                : "cooked 2048-byte ISO";

            bool hasManifestWarnings = inspection.MissingHashEntryCount > 0 || inspection.UnmappedHashEntryCount > 0;
            bool hasNeroNriWarnings = inspection.NeroNriWarnings.Count > 0;
            string manifestWarningSummary = inspection.MissingHashEntryCount > 0 && inspection.UnmappedHashEntryCount > 0
                ? $" WARNING: {inspection.MissingHashEntryCount:N0} ISO file(s) have no manifest hash; {inspection.UnmappedHashEntryCount:N0} manifest entry/entries are unused."
                : inspection.MissingHashEntryCount > 0
                    ? $" WARNING: {inspection.MissingHashEntryCount:N0} ISO file(s) have no manifest hash."
                    : inspection.UnmappedHashEntryCount > 0
                        ? $" WARNING: {inspection.UnmappedHashEntryCount:N0} manifest entry/entries are unused."
                        : string.Empty;
            string neroWarningSummary = hasNeroNriWarnings
                ? $" WARNING: {inspection.NeroNriWarnings.Count:N0} required Nero NRI payload(s) are unavailable."
                : string.Empty;
            SkeletonInspectionText.Text =
                $"{kind}; {inspection.SectorCount:N0} sectors; volume '{inspection.VolumeIdentifier}'; " +
                $"{normalFiles:N0} ISO files, {special:N0} special/hash-only entries, {inspection.HashEntryCount:N0} manifest hashes." +
                manifestWarningSummary + neroWarningSummary;
            SkeletonProgressText.Text = hasManifestWarnings || hasNeroNriWarnings ? "Loaded with warning" : "Loaded";
            AppendSkeletonLog($"Detected {kind}.");
            AppendSkeletonLog($"ISO9660 volume: {inspection.VolumeIdentifier}");
            AppendSkeletonLog($"ISO files: {normalFiles:N0}; manifest entries: {inspection.HashEntryCount:N0}; unmapped hashes: {inspection.UnmappedHashEntryCount:N0}.");
            if (inspection.NeroSystemAreaRecovery is { } nero)
            {
                AppendSkeletonLog(
                    $"NERO SYSTEM_AREA: hidden project {nero.ProjectFileName} detected at LBA {nero.ProjectExtentLba:N0} " +
                    $"({nero.ProjectDataLength:N0} bytes, {nero.NeroIsoSignature}). Its missing four-byte system-area value will be recovered during resurrection.");
            }
            foreach (string warning in inspection.NeroNriWarnings)
                AppendSkeletonLog("WARNING — NERO NRI: " + warning);

            if (inspection.MissingHashEntryCount > 0)
            {
                AppendSkeletonLog($"WARNING: The .hash file has no exact pathname/hash entry for {inspection.MissingHashEntryCount:N0} file(s) in the skeleton file table:");
                foreach (string path in inspection.FilesMissingFromHashManifest.Take(100))
                    AppendSkeletonLog($"  Missing from .hash: {path}");
                if (inspection.MissingHashEntryCount > 100)
                    AppendSkeletonLog($"  ...and {inspection.MissingHashEntryCount - 100:N0} more.");

            }

            if (inspection.UnmappedHashEntryCount > 0)
            {
                SkeletonContentEntry[] ignoredEntries = inspection.Entries
                    .Where(entry => entry.SpecialKind == SkeletonSpecialKind.UnmappedHashEntry)
                    .ToArray();
                AppendSkeletonLog($"WARNING: The .hash file contains {inspection.UnmappedHashEntryCount:N0} entry/entries that are not used by the skeleton and will be ignored:");
                foreach (SkeletonContentEntry entry in ignoredEntries.Take(100))
                    AppendSkeletonLog($"  Ignored .hash entry: {entry.Path}");
                if (ignoredEntries.Length > 100)
                    AppendSkeletonLog($"  ...and {ignoredEntries.Length - 100:N0} more.");
            }

            if (hasManifestWarnings || hasNeroNriWarnings)
            {
                string? manifestWarning = inspection.MissingHashEntryCount > 0 && inspection.UnmappedHashEntryCount > 0
                    ? $"The .hash file is missing entries for {inspection.MissingHashEntryCount:N0} file(s) listed in the skeleton and contains {inspection.UnmappedHashEntryCount:N0} unused entry/entries. The unused entries will be ignored."
                    : inspection.MissingHashEntryCount > 0
                        ? $"The .hash file is missing entries for {inspection.MissingHashEntryCount:N0} file(s) listed in the skeleton. Those files cannot be matched."
                        : inspection.UnmappedHashEntryCount > 0
                            ? $"The .hash file contains {inspection.UnmappedHashEntryCount:N0} entry/entries that are not used by the skeleton. They will be ignored."
                            : null;

                var warningParts = new List<string>();
                if (manifestWarning is not null)
                    warningParts.Add(manifestWarning);
                warningParts.AddRange(inspection.NeroNriWarnings);

                await ShowMessageAsync(
                    "DumpToolbox — SkeleTool warning",
                    string.Join("\n\n", warningParts) + "\n\nSee the activity log for the affected paths.");
            }
        }
        catch (OperationCanceledException)
        {
            SkeletonProgressBar.IsIndeterminate = false;
            AppendSkeletonLog("Load cancelled.");
            SkeletonProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            SkeletonProgressBar.IsIndeterminate = false;
            AppendSkeletonLog($"ERROR: {ex.Message}");
            SkeletonProgressText.Text = "Error";
            await ShowMessageAsync("DumpToolbox — Skeletool", ex.Message);
        }
        finally
        {
            _skeletonCts?.Dispose();
            _skeletonCts = null;
            SetSkeletonRunning(false);
            UpdateSkeletonActionButtons();
        }
    }

    private async void SkeletonScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_skeletonCts is not null || _skeletonInspection is null)
            return;

        try
        {
            string folder = SkeletonSourceFolderBox.Text?.Trim() ?? string.Empty;
            bool recursive = SkeletonRecursiveCheckBox.IsChecked != false;
            bool forceRehash = SkeletonForceRehashCheckBox.IsChecked == true;
            SkeletonProgressBar.Value = 0;
            SkeletonProgressText.Text = "Hashing...";
            AppendSkeletonLog($"Hashing source files in: {folder}" + (recursive ? " (recursive)" : string.Empty));
            AppendSkeletonLog(forceRehash
                ? "Hash cache bypassed: Force rehash is enabled."
                : "Using .dumptoolbox_hashcache.json when file size and modification time are unchanged.");

            _skeletonCts = new CancellationTokenSource();
            SetSkeletonRunning(true);
            var stopwatch = Stopwatch.StartNew();
            int lastFilesHashed = 0;
            int lastFilesCached = 0;
            int lastFilesSkipped = 0;
            var progress = new Progress<SkeletonSourceScanProgress>(p =>
            {
                SkeletonProgressBar.Value = p.Fraction * 100;
                double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double speed = p.BytesHashed / 1048576.0 / seconds;
                lastFilesHashed = p.FilesHashed;
                lastFilesCached = p.FilesCached;
                lastFilesSkipped = p.FilesSkipped;
                SkeletonProgressText.Text = $"{p.FilesProcessed:N0}/{p.FilesTotal:N0}  hashed {p.FilesHashed:N0}  cached {p.FilesCached:N0}  skipped {p.FilesSkipped:N0}  {speed:N1} MiB/s";
                SetWindowStatus($"Skeletool — hashing {p.FilesProcessed}/{p.FilesTotal}");

                if (!string.IsNullOrWhiteSpace(p.DetectedNeroProject))
                    AppendSkeletonLog("NERO NRI: " + p.DetectedNeroProject);

                if (!string.IsNullOrWhiteSpace(p.MatchedEntryPath) &&
                    _skeletonNodes.TryGetValue(p.MatchedEntryPath, out SkeletonTreeNode? node))
                {
                    node.Status = p.MatchedAsXa ? "✓XA" : "✓";
                    node.SourcePath = p.MatchedSourcePath;
                }
            });

            IReadOnlyDictionary<string, SkeletonSourceMatch> found = await _skeletonService.MatchSourcesAsync(
                _skeletonInspection,
                folder,
                recursive,
                forceRehash,
                false,
                progress,
                _skeletonCts.Token);

            stopwatch.Stop();
            _skeletonMatches = MergeSkeletonMatches(_skeletonMatches, found);
            MarkSkeletonMissingStatuses();
            int required = CountSkeletonRequiredMatches(_skeletonInspection);
            SkeletonProgressBar.Value = 100;
            SkeletonProgressText.Text = $"{_skeletonMatches.Count:N0}/{required:N0} matched";
            AppendSkeletonLog($"Source scan complete in {stopwatch.Elapsed}. Matched {_skeletonMatches.Count:N0} of {required:N0} required entries.");
            AppendSkeletonLog($"Hash cache stats: {lastFilesCached:N0} reused, {lastFilesHashed:N0} freshly hashed, {lastFilesSkipped:N0} skipped.");
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendSkeletonLog("Source hashing cancelled.");
            SkeletonProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendSkeletonLog($"ERROR: {ex.Message}");
            SkeletonProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Skeletool", ex.Message);
        }
        finally
        {
            _skeletonCts?.Dispose();
            _skeletonCts = null;
            SetSkeletonRunning(false);
            UpdateSkeletonActionButtons();
        }
    }

    private async void SkeletonScanImageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_skeletonCts is not null || _skeletonInspection is null) return;
        try
        {
            string image = SkeletonSourceImageBox.Text?.Trim() ?? string.Empty;
            SkeletonProgressBar.Value = 0;
            SkeletonProgressText.Text = "Scanning image...";
            AppendSkeletonLog($"Scanning source disc image: {image}");
            _skeletonCts = new CancellationTokenSource();
            SetSkeletonRunning(true);
            var progress = new Progress<SkeletonSourceScanProgress>(p =>
            {
                SkeletonProgressBar.Value = p.Fraction * 100;
                SkeletonProgressText.Text = $"{p.FilesProcessed:N0}/{p.FilesTotal:N0} files";
                if (!string.IsNullOrWhiteSpace(p.DetectedNeroProject))
                    AppendSkeletonLog("NERO NRI: " + p.DetectedNeroProject);
            });
            IReadOnlyDictionary<string, SkeletonSourceMatch> found = await _skeletonService.MatchSourceImageAsync(
                _skeletonInspection, image, false, progress, _skeletonCts.Token);
            _skeletonMatches = MergeSkeletonMatches(_skeletonMatches, found);
            MarkSkeletonMissingStatuses();
            AppendSkeletonLog($"Source image scan complete. Added/retained {found.Count:N0} matching entry/entries; cumulative matches: {_skeletonMatches.Count:N0}.");
            SkeletonProgressBar.Value = 100;
            SkeletonProgressText.Text = $"{_skeletonMatches.Count:N0} matched";
        }
        catch (OperationCanceledException) { AppendSkeletonLog("Source image scan cancelled."); }
        catch (Exception ex) { AppendSkeletonLog($"ERROR: {ex.Message}"); await ShowMessageAsync("DumpToolbox — Skeletool", ex.Message); }
        finally
        {
            _skeletonCts?.Dispose(); _skeletonCts = null; SetSkeletonRunning(false); UpdateSkeletonActionButtons();
        }
    }

    private static IReadOnlyDictionary<string, SkeletonSourceMatch> MergeSkeletonMatches(
        IReadOnlyDictionary<string, SkeletonSourceMatch> existing,
        IReadOnlyDictionary<string, SkeletonSourceMatch> incoming)
    {
        var merged = new Dictionary<string, SkeletonSourceMatch>(existing, StringComparer.OrdinalIgnoreCase);
        foreach ((string path, SkeletonSourceMatch match) in incoming)
        {
            if (!merged.TryGetValue(path, out SkeletonSourceMatch? old) ||
                old.MatchMethod.StartsWith("SHA-1 catalogue", StringComparison.OrdinalIgnoreCase) ||
                (old.IsXa && !match.IsXa))
            {
                merged[path] = match;
            }
        }
        return merged;
    }


    private async void SkeletonCheckSha1DbButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_skeletonCts is not null || _skeletonInspection is null)
            return;

        if (!IsSha1DatabaseEnabled)
        {
            await ShowMessageAsync("DumpToolbox — Skeletool", "The SHA-1 catalogue is currently disabled in Settings.");
            return;
        }

        try
        {
            SkeletonProgressBar.Value = 0;
            SkeletonProgressText.Text = "Checking SHA-1 DB...";
            AppendSkeletonLog("Checking SHA-1 catalogue for matching payloads...");

            _skeletonCts = new CancellationTokenSource();
            SetSkeletonRunning(true);

            int matchedBefore = _skeletonMatches.Count;
            IReadOnlyDictionary<string, SkeletonSourceMatch> catalogueMatches =
                await _skeletoolCatalogueService.FindMatchesAsync(_skeletonInspection, _skeletonCts.Token);

            _skeletonMatches = MergeSkeletonMatches(_skeletonMatches, catalogueMatches);
            MarkSkeletonMissingStatuses();

            int matchedAfter = _skeletonMatches.Count;
            int added = Math.Max(0, matchedAfter - matchedBefore);
            int required = CountSkeletonRequiredMatches(_skeletonInspection);
            SkeletonProgressBar.Value = 100;
            SkeletonProgressText.Text = $"{matchedAfter:N0}/{required:N0} matched";

            if (catalogueMatches.Count == 0)
                AppendSkeletonLog("SHA-1 catalogue check complete. No matching payloads were found.");
            else
                AppendSkeletonLog($"SHA-1 catalogue check complete. Considered {catalogueMatches.Count:N0} matching payload(s); {added:N0} new match(es) are now available for resurrection.");
        }
        catch (OperationCanceledException)
        {
            AppendSkeletonLog("SHA-1 catalogue check cancelled.");
            SkeletonProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendSkeletonLog($"ERROR: {ex.Message}");
            SkeletonProgressText.Text = "Error";
            await ShowMessageAsync("DumpToolbox — Skeletool", ex.Message);
        }
        finally
        {
            _skeletonCts?.Dispose();
            _skeletonCts = null;
            SetSkeletonRunning(false);
            UpdateSkeletonActionButtons();
        }
    }

    private static int CountSkeletonRequiredMatches(SkeletonInspectionResult inspection)
    {
        bool neroSystemAreaIsGenerated = SkeletonResurrectionService.CanRecoverNeroSystemArea(inspection);
        return inspection.Entries.Count(e => e.CanRestore && !e.IsEmpty &&
            !(e.SpecialKind == SkeletonSpecialKind.SystemArea &&
              (string.Equals(e.Sha1, SkeletonResurrectionService.ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) ||
               neroSystemAreaIsGenerated)) &&
            (!string.IsNullOrWhiteSpace(e.Sha1) || !string.IsNullOrWhiteSpace(e.XaSha1)));
    }

    private async void SkeletonResurrectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_skeletonCts is not null || _skeletonInspection is null)
            return;

        try
        {
            string output = SkeletonOutputBox.Text?.Trim() ?? string.Empty;
            bool allowMissing = SkeletonAllowMissingCheckBox.IsChecked == true;
            SkeletonProgressBar.Value = 0;
            SkeletonProgressText.Text = "Preparing...";
            AppendSkeletonLog($"Resurrecting to: {output}");
            AppendSkeletonLog(allowMissing
                ? "Partial resurrection enabled: missing payloads remain zeroed."
                : "Complete resurrection required: missing payloads will abort the operation.");

            _skeletonCts = new CancellationTokenSource();
            SetSkeletonRunning(true);
            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<SkeletonResurrectionProgress>(p =>
            {
                SkeletonProgressBar.Value = p.Fraction * 100;
                SkeletonProgressText.Text = $"{p.Fraction:P0}  {p.Message}";
                SetWindowStatus($"Skeletool — {p.Message}");

                if (!string.IsNullOrWhiteSpace(p.EntryPath) &&
                    _skeletonNodes.TryGetValue(p.EntryPath, out SkeletonTreeNode? node))
                {
                    if (p.Kind == SkeletonResurrectionEventKind.RestoringEntry)
                        node.Status = "…";
                    else if (p.Kind == SkeletonResurrectionEventKind.EntryRestored)
                        node.Status = "✓R";
                }
            });
            var activity = new Progress<string>(AppendSkeletonLog);

            IReadOnlyDictionary<string, SkeletonSourceMatch> resurrectionMatches = _skeletonMatches;
            int deferredCatalogueCount = resurrectionMatches.Values.Count(m => m.CatalogueSource is not null);
            if (deferredCatalogueCount > 0)
            {
                SkeletonProgressText.Text = "Preparing catalogue payloads...";
                AppendSkeletonLog($"SHA-1 catalogue: {deferredCatalogueCount:N0} selected payload(s) require on-demand materialization for this rebuild.");
                resurrectionMatches = await _skeletoolCatalogueService.MaterializeMatchesForResurrectionAsync(
                    resurrectionMatches, activity, _skeletonCts.Token);
            }

            NeroRecoveryDialogState? neroDialog = null;
            IProgress<NeroSystemAreaRecoveryProgress>? neroProgress = null;
            if (SkeletonResurrectionService.CanRecoverNeroSystemArea(_skeletonInspection) &&
                !resurrectionMatches.ContainsKey("SYSTEM_AREA"))
            {
                neroDialog = ShowNeroRecoveryDialog(_skeletonInspection.NeroSystemAreaRecovery!);
                neroProgress = new Progress<NeroSystemAreaRecoveryProgress>(p => UpdateNeroRecoveryDialog(neroDialog, p));
            }

            SkeletonResurrectionResult result;
            try
            {
                result = await _skeletonService.ResurrectAsync(
                    _skeletonInspection,
                    resurrectionMatches,
                    output,
                    allowMissing,
                    progress,
                    activity,
                    _skeletonCts.Token,
                    ResolveEofSlackAmbiguityAsync,
                    neroProgress);
            }
            finally
            {
                CloseNeroRecoveryDialog(neroDialog);
            }

            stopwatch.Stop();
            SkeletonProgressBar.Value = 100;
            SkeletonProgressText.Text = $"Complete — {result.RestoredEntries:N0} restored";
            AppendSkeletonLog($"Complete in {stopwatch.Elapsed}. Restored/satisfied {result.RestoredEntries:N0} entries; {result.MissingEntries:N0} remain missing.");
            AppendSkeletonLog($"Created: {result.OutputPath} ({result.OutputBytes:N0} bytes)");
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendSkeletonLog("Resurrection cancelled. Partial output removed.");
            SkeletonProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendSkeletonLog($"ERROR: {ex.Message}");
            SkeletonProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Skeletool", ex.Message);
        }
        finally
        {
            _skeletonCts?.Dispose();
            _skeletonCts = null;
            SetSkeletonRunning(false);
            UpdateSkeletonActionButtons();
        }
    }

    private void SkeletonCancelButton_Click(object? sender, RoutedEventArgs e) => _skeletonCts?.Cancel();

    private void BuildSkeletonTree(SkeletonInspectionResult inspection)
    {
        _skeletonTreeRoots.Clear();
        _skeletonNodes.Clear();

        var rootFolders = new Dictionary<string, SkeletonTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonContentEntry entry in inspection.Entries.Where(e => e.SpecialKind == SkeletonSpecialKind.None))
        {
            string[] parts = entry.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            ObservableCollection<SkeletonTreeNode> children = _skeletonTreeRoots;
            string accumulated = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                accumulated += "/" + parts[i];
                if (!rootFolders.TryGetValue(accumulated, out SkeletonTreeNode? folder))
                {
                    folder = new SkeletonTreeNode(parts[i]);
                    rootFolders[accumulated] = folder;
                    children.Add(folder);
                }
                children = folder.Children;
            }

            var fileNode = new SkeletonTreeNode(parts[^1], entry);
            SetInitialSkeletonNodeStatus(fileNode, entry);
            children.Add(fileNode);
            _skeletonNodes[entry.Path] = fileNode;
        }

        // Keep directory branches together instead of interleaving them with root
        // files according to the full ISO path. Apply the same folders-first order
        // recursively within every directory.
        SortSkeletonTreeNodes(_skeletonTreeRoots);

        SkeletonContentEntry[] special = inspection.Entries.Where(e => e.SpecialKind != SkeletonSpecialKind.None).ToArray();
        if (special.Length > 0)
        {
            var specialRoot = new SkeletonTreeNode("[Special / hash-only entries]");
            foreach (SkeletonContentEntry entry in special)
            {
                var node = new SkeletonTreeNode(entry.Path, entry);
                SetInitialSkeletonNodeStatus(node, entry);
                if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                    SkeletonResurrectionService.CanRecoverNeroSystemArea(inspection))
                {
                    node.Status = "NRI";
                    node.SourcePath = $"Will generate from {inspection.NeroSystemAreaRecovery!.ProjectFileName}";
                }
                specialRoot.Children.Add(node);
                _skeletonNodes[entry.Path] = node;
            }
            _skeletonTreeRoots.Add(specialRoot);
        }
    }

    private static void SortSkeletonTreeNodes(ObservableCollection<SkeletonTreeNode> nodes)
    {
        SkeletonTreeNode[] ordered = nodes
            .OrderBy(node => node.IsFolder ? 0 : 1)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (SkeletonTreeNode folder in ordered.Where(node => node.IsFolder))
            SortSkeletonTreeNodes(folder.Children);

        nodes.Clear();
        foreach (SkeletonTreeNode node in ordered)
            nodes.Add(node);
    }

    private static void SetInitialSkeletonNodeStatus(SkeletonTreeNode node, SkeletonContentEntry entry)
    {
        node.SourcePath = null;
        if (entry.SpecialKind == SkeletonSpecialKind.UnmappedHashEntry)
            node.Status = "Ignored";
        else if (!entry.CanRestore)
            node.Status = "!";
        else if (entry.IsEmpty)
            node.Status = "✓0";
        else if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                 string.Equals(entry.Sha1, SkeletonResurrectionService.ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            node.Status = "✓0";
        else if (string.IsNullOrWhiteSpace(entry.Sha1) && string.IsNullOrWhiteSpace(entry.XaSha1))
            node.Status = "?";
        else
            node.Status = "○";
    }

    private void MarkSkeletonMissingStatuses()
    {
        if (_skeletonInspection is null)
            return;

        foreach (SkeletonContentEntry entry in _skeletonInspection.Entries)
        {
            if (!_skeletonNodes.TryGetValue(entry.Path, out SkeletonTreeNode? node))
                continue;

            if (entry.SpecialKind == SkeletonSpecialKind.UnmappedHashEntry)
            {
                node.Status = "Ignored";
                node.SourcePath = null;
                continue;
            }

            if (_skeletonMatches.TryGetValue(entry.Path, out SkeletonSourceMatch? match))
            {
                node.Status = match.IsXa ? "✓XA" : "✓";
                node.SourcePath = match.SourcePath;
            }
            else if (entry.CanRestore && !entry.IsEmpty &&
                     !(entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                       (string.Equals(entry.Sha1, SkeletonResurrectionService.ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) ||
                        SkeletonResurrectionService.CanRecoverNeroSystemArea(_skeletonInspection))) &&
                     (!string.IsNullOrWhiteSpace(entry.Sha1) || !string.IsNullOrWhiteSpace(entry.XaSha1)))
            {
                node.Status = "✗";
                node.SourcePath = null;
            }
            else if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                     SkeletonResurrectionService.CanRecoverNeroSystemArea(_skeletonInspection))
            {
                node.Status = "NRI";
                node.SourcePath = $"Will generate from {_skeletonInspection.NeroSystemAreaRecovery!.ProjectFileName}";
            }
        }
    }

    private sealed class NeroRecoveryDialogState
    {
        public NeroRecoveryDialogState(Window window, ProgressBar progressBar, TextBlock status, TextBlock details, Button cancelButton)
        {
            Window = window;
            ProgressBar = progressBar;
            Status = status;
            Details = details;
            CancelButton = cancelButton;
        }

        public Window Window { get; }
        public ProgressBar ProgressBar { get; }
        public TextBlock Status { get; }
        public TextBlock Details { get; }
        public Button CancelButton { get; }
        public bool ClosingProgrammatically { get; set; }
    }

    private NeroRecoveryDialogState ShowNeroRecoveryDialog(NeroSystemAreaRecoveryInfo info)
    {
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 18 };
        var status = new TextBlock
        {
            Text = "Preparing the four-byte search…",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var details = new TextBlock
        {
            Text = "This may take several minutes. DumpToolbox will use the available CPU cores while keeping one logical processor free for the interface.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var cancel = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 90
        };
        var dialog = new Window
        {
            Title = "Recovering Nero system area",
            Width = 620,
            MinHeight = 260,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Hidden Nero project: {info.ProjectFileName}  •  LBA {info.ProjectExtentLba:N0}  •  {info.ProjectDataLength:N0} bytes",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "The skeleton identifies 28 bytes of Nero's 32-byte system-area record. The manifest SHA-1 is being used to recover the remaining four private bytes.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    progressBar,
                    status,
                    details,
                    cancel
                }
            }
        };

        var state = new NeroRecoveryDialogState(dialog, progressBar, status, details, cancel);
        ApplyThemeClassToWindow(dialog);
        cancel.Click += (_, _) =>
        {
            cancel.IsEnabled = false;
            status.Text = "Cancelling…";
            _skeletonCts?.Cancel();
        };
        dialog.Closed += (_, _) =>
        {
            if (!state.ClosingProgrammatically)
                _skeletonCts?.Cancel();
        };
        _ = dialog.ShowDialog(this);
        return state;
    }

    private void UpdateNeroRecoveryDialog(
        NeroRecoveryDialogState state,
        NeroSystemAreaRecoveryProgress progress)
    {
        if (state.ClosingProgrammatically)
            return;

        state.ProgressBar.Value = progress.Fraction * 100;
        double rate = progress.CandidatesPerSecond;
        state.Status.Text = progress.PrivateValue is uint value
            ? $"Recovered private bytes {value:X8}; SHA-1 matched."
            : $"{progress.Fraction:P1} — {progress.CandidatesTested:N0} of {progress.TotalCandidates:N0} candidates tested";

        if (progress.PrivateValue is not null)
        {
            state.Details.Text = $"Completed in {progress.Elapsed:g}. Resurrection will now continue.";
            CloseNeroRecoveryDialog(state);
            return;
        }

        if (rate > 0)
        {
            double secondsRemaining = (progress.TotalCandidates - progress.CandidatesTested) / rate;
            TimeSpan remaining = TimeSpan.FromSeconds(Math.Min(secondsRemaining, TimeSpan.MaxValue.TotalSeconds));
            state.Details.Text = $"{rate / 1_000_000:N2} million candidates/second • worst-case remaining {remaining:g}";
        }
        SetWindowStatus($"Skeletool — recovering Nero system area {progress.Fraction:P0}");
    }

    private static void CloseNeroRecoveryDialog(NeroRecoveryDialogState? state)
    {
        if (state is null || state.ClosingProgrammatically)
            return;

        state.ClosingProgrammatically = true;
        state.Window.Close();
    }

    private void SetSkeletonRunning(bool running)
    {
        SkeletonCancelButton.IsEnabled = running;
        SkeletonBrowseButton.IsEnabled = !running;
        SkeletonHashBrowseButton.IsEnabled = !running;
        SkeletonSourceBrowseButton.IsEnabled = !running;
        SkeletonSourceImageBrowseButton.IsEnabled = !running;
        SkeletonOutputBrowseButton.IsEnabled = !running;
        SkeletonLoadButton.IsEnabled = !running;
        SkeletonRecursiveCheckBox.IsEnabled = !running;
        SkeletonAllowMissingCheckBox.IsEnabled = !running;
        SkeletonForceRehashCheckBox.IsEnabled = !running;
        SkeletonPathBox.IsReadOnly = running;
        SkeletonHashBox.IsReadOnly = running;
        SkeletonSourceFolderBox.IsReadOnly = running;
        SkeletonSourceImageBox.IsReadOnly = running;
        SkeletonOutputBox.IsReadOnly = running;
        if (running)
        {
            SkeletonScanButton.IsEnabled = false;
            SkeletonScanImageButton.IsEnabled = false;
            SkeletonCheckSha1DbButton.IsEnabled = false;
            SkeletonResurrectButton.IsEnabled = false;
        }
    }

    private void UpdateSkeletonActionButtons()
    {
        bool loaded = _skeletonInspection is not null && _skeletonCts is null;
        SkeletonScanButton.IsEnabled = loaded;
        SkeletonScanImageButton.IsEnabled = loaded;
        SkeletonCheckSha1DbButton.IsEnabled = loaded && IsSha1DatabaseEnabled;
        SkeletonResurrectButton.IsEnabled = loaded;
    }

    private void AppendSkeletonLog(string message)
    {
        string timestamp = $"[{DateTime.Now:HH:mm:ss}] ";
        var line = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap
        };

        line.Inlines!.Add(new Run(timestamp));

        // Colour only the final verification status token. Check MISMATCH first
        // because it contains the substring MATCH. All preceding text remains in
        // the normal theme foreground so filenames/hashes are not colourised.
        Match status = Regex.Match(message, @"(?<status>MISMATCH|MATCH)$", RegexOptions.CultureInvariant);
        if (status.Success)
        {
            if (status.Index > 0)
                line.Inlines.Add(new Run(message[..status.Index]));

            line.Inlines.Add(new Run(status.Value)
            {
                Foreground = status.Value == "MATCH" ? Brushes.Green : Brushes.Red
            });
        }
        else
        {
            line.Inlines.Add(new Run(message));
        }

        SkeletonLogPanel.Children.Add(line);

        if (_skeletonActivityLogText.Length > 0)
            _skeletonActivityLogText.AppendLine();
        _skeletonActivityLogText.Append(timestamp).Append(message);
        UpdateSkeletonDetachedLog();

        Dispatcher.UIThread.Post(() =>
        {
            double bottom = Math.Max(0, SkeletonLogScroll.Extent.Height - SkeletonLogScroll.Viewport.Height);
            SkeletonLogScroll.Offset = new Vector(0, bottom);
        }, DispatcherPriority.Background);
    }
}
