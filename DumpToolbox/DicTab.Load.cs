using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private async void DicLoadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dicCts is not null)
            return;

        string selectedLogLocation = DicLogPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedLogLocation))
        {
            await ShowMessageAsync("DumpToolbox — DIC", "Choose the folder containing the DiscImageCreator logs first.");
            return;
        }

        try
        {
            ClearDicLog();
            _dicTreeRoots.Clear();
            _dicNodes.Clear();
            _dicMatches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
            _dicAppliedEntries.Clear();
            _dicInspection = null;
            _dicLogs = null;
            _dicState = null;
            _dicStatePath = null;
            _dicDonorRequirementsSatisfied = false;
            _dicCoverageAudit = Array.Empty<DicRecoveryCoverageItem>();
            DicProgressBar.Value = 0;
            DicProgressText.Text = "Reading DIC logs...";
            AppendDicLog($"Loading DiscImageCreator log set from folder: {selectedLogLocation}");

            DicLogSet discovered = _dicLogImportService.Discover(selectedLogLocation);

            if (DicForceRehashCheckBox.IsChecked == true)
            {
                string forcedStatePath = _dicRecoveryStateService.GetStatePath(discovered);
                string forcedDonorCache = Path.Combine(discovered.Directory, ".dumptoolbox_dic_donor_cache");
                bool clearedAnything = false;

                if (File.Exists(forcedStatePath))
                {
                    File.Delete(forcedStatePath);
                    clearedAnything = true;
                }

                if (Directory.Exists(forcedDonorCache))
                {
                    Directory.Delete(forcedDonorCache, recursive: true);
                    clearedAnything = true;
                }

                AppendDicLog(clearedAnything
                    ? "Force rehash / clear cache: removed saved DIC recovery state and donor cache; starting a clean recovery session."
                    : "Force rehash / clear cache: no previous DIC recovery cache was present; starting a clean recovery session.");
            }

            _dicLogs = discovered;
            AppendDicLog($"DIC basename: {discovered.BaseName}");
            AppendDicLog($"volDesc:  {(discovered.VolDescPath is null ? "missing" : Path.GetFileName(discovered.VolDescPath))}");
            AppendDicLog($"disc:     {(discovered.DiscPath is null ? "missing" : Path.GetFileName(discovered.DiscPath))}");
            AppendDicLog($"EccEdc:   {(discovered.EccEdcPath is null ? "missing" : Path.GetFileName(discovered.EccEdcPath))}");
            AppendDicLog($"mainInfo: {(discovered.MainInfoPath is null ? "missing" : Path.GetFileName(discovered.MainInfoPath))}");
            AppendDicLog($"mainError:{(discovered.MainErrorPath is null ? " missing" : " " + Path.GetFileName(discovered.MainErrorPath))}");
            AppendDicLog($"dat:      {(discovered.DatPath is null ? "missing" : Path.GetFileName(discovered.DatPath))}");

            _dicCts = new CancellationTokenSource();
            SetDicRunning(true);

            var progress = new Progress<DicImportProgress>(p =>
            {
                DicProgressBar.Value = p.Fraction * 100;
                DicProgressText.Text = p.Message;
                SetWindowStatus($"DIC — {p.Message}");
            });

            DicImportResult result = await _dicLogImportService.ImportAsync(
                selectedLogLocation,
                progress,
                _dicCts.Token);

            SkeletonInspectionResult inspection = result.Inspection;
            _dicStatePath = _dicRecoveryStateService.GetStatePath(discovered);
            DicRecoveryStateLoadResult stateLoad = await _dicRecoveryStateService.LoadAsync(
                _dicStatePath,
                discovered,
                inspection,
                _dicCts.Token);
            // Keep a non-null local for the remainder of this load operation.
            // _dicState is a nullable field because no DIC session may be loaded,
            // so using the field directly here causes nullable-flow warnings even
            // though LoadAsync always returns a concrete State object.
            DicRecoveryState state = stateLoad.State;
            _dicState = state;
            _dicMatches = stateLoad.Matches
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            // JSON can technically populate a collection property with null even when
            // the model supplies a non-null default. Capture a guaranteed non-null
            // collection locally so nullable flow analysis does not have to reason
            // about a mutable property being dereferenced again later in the method.
            List<string> savedAppliedEntries = state.AppliedEntries ??= new List<string>();
            foreach (string path in savedAppliedEntries)
                _dicAppliedEntries.Add(path);

            bool resumedWorkingImage = false;
            string? lastOutputPath = state.LastOutputPath;
            if (_dicAppliedEntries.Count > 0 &&
                !string.IsNullOrWhiteSpace(lastOutputPath) &&
                File.Exists(lastOutputPath))
            {
                try
                {
                    long expectedLength = checked(inspection.SectorCount * SkeletonResurrectionService.RawSectorSize);
                    if (new FileInfo(lastOutputPath).Length == expectedLength)
                    {
                        string workingPath = Path.GetFullPath(lastOutputPath);
                        IReadOnlyList<SkeletonContentEntry> adjustedEntries = inspection.Entries
                            .Select(entry => _dicAppliedEntries.Contains(entry.Path)
                                ? entry with { RequiresSource = false }
                                : entry)
                            .ToArray();
                        inspection = inspection with { SkeletonPath = workingPath, Entries = adjustedEntries };
                        resumedWorkingImage = true;

                        // Already-applied payloads live in the working BIN now. Keep
                        // their old source references in the persistent state as a
                        // fallback in case the cumulative BIN is later moved/deleted;
                        // rebuild passes filter them out while the working BIN exists.
                    }
                }
                catch
                {
                    resumedWorkingImage = false;
                }
            }

            if (!resumedWorkingImage && _dicAppliedEntries.Count > 0)
            {
                AppendDicLog("WARNING: saved applied-file state was found but its last rebuilt BIN is unavailable or has the wrong size. Those entries will need to be sourced again before a new complete rebuild.");
                _dicAppliedEntries.Clear();
                savedAppliedEntries.Clear();
            }

            bool donorRequirementsRequired = inspection.DonorRequirements?.Any(requirement => requirement.BlocksResurrection) == true;
            _dicDonorRequirementsSatisfied = !donorRequirementsRequired ||
                                           (resumedWorkingImage && state.DonorRequirementsSatisfied);

            _dicInspection = inspection;
            _dicCoverageAudit = result.CoverageAudit;
            DicLogPathBox.Text = selectedLogLocation;
            string? savedDonorPath = state.LastDonorImagePath;
            if (!string.IsNullOrWhiteSpace(savedDonorPath) && File.Exists(savedDonorPath))
            {
                DicDonorImageBox.Text = savedDonorPath;

                // If there is no cumulative output BIN yet, re-apply a previously
                // selected same-disc donor automatically. ImportAsync regenerates the
                // synthetic skeleton on each load, so otherwise the donor's exact
                // primary ISO9660 metadata would be lost even though its cached file
                // matches remain valid.
                if (!resumedWorkingImage)
                {
                    try
                    {
                        AppendDicLog($"Re-applying saved donor image: {savedDonorPath}");
                        string cacheRoot = Path.Combine(discovered.Directory, ".dumptoolbox_dic_donor_cache");
                        DicDonorScanResult savedDonor = await _dicDonorImageService.MatchAsync(
                            inspection,
                            savedDonorPath,
                            cacheRoot,
                            applySameDiscMetadata: true,
                            progress: null,
                            cancellationToken: _dicCts.Token);
                        MergeDicMatches(savedDonor.Matches);
                        if (donorRequirementsRequired)
                        {
                            _dicDonorRequirementsSatisfied = savedDonor.DonorRequirementsSatisfied;
                            state.DonorRequirementsSatisfied = _dicDonorRequirementsSatisfied;
                        }
                        if (savedDonor.SameDisc)
                        {
                            AppendDicLog($"Saved donor identity matched; re-applied {savedDonor.MetadataSectorsApplied:N0} original primary ISO9660 metadata sector(s).");
                            if (savedDonor.RequiredPayloadsApplied > 0)
                                AppendDicLog($"Saved donor also restored {savedDonor.RequiredPayloadsApplied:N0} mandatory ISO 9660 payload region(s).");
                            if (savedDonor.OptionalExactnessRegionsApplied > 0)
                                AppendDicLog($"Saved donor also restored {savedDonor.OptionalExactnessRegionsApplied:N0} optional exactness region(s) (system/slack/post-volume/metadata).");
                        }
                        else
                            AppendDicLog("Saved donor no longer passes the exact PVD+volume-label identity check; it was used only as a recursive file source.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (donorRequirementsRequired)
                        {
                            _dicDonorRequirementsSatisfied = false;
                            state.DonorRequirementsSatisfied = false;
                        }
                        AppendDicLog($"WARNING: saved donor could not be re-applied: {ex.Message}");
                    }
                }
            }
            DicOutputBox.Text = _skeletonService.SuggestOutputPath(inspection);
            BuildDicTree(inspection);
            MarkDicMissingStatuses();

            int associatedSourceCount = inspection.Entries.Count(entry => (entry.IsoFileFlags & 0x04) != 0 && entry.RequiresSource && !entry.IsEmpty);
            int filesToRestore = inspection.Entries.Count(IsRequiredDicSourceEntry);
            int disabled = inspection.Entries.Count(e => !e.CanRestore);
            int queuedMatches = CountQueuedDicMatches();
            int satisfied = _dicAppliedEntries.Count + queuedMatches;
            string dicImageFormat = inspection.ImageKind == SkeletonImageKind.Cooked2048
                ? "Cooked 2048-byte DIC reconstruction"
                : "Raw 2352-byte DIC reconstruction";
            string filesystemSummary = inspection.DicHfsPartitions is { Count: > 0 }
                ? "ISO9660 + HFS hybrid"
                : "ISO9660 only";
            DicInspectionText.Text =
                $"{dicImageFormat} ({filesystemSummary}); {inspection.SectorCount:N0} sectors; " +
                $"volume '{inspection.VolumeIdentifier}'; {filesToRestore:N0} payloads still required; " +
                $"{_dicAppliedEntries.Count:N0} already applied; {queuedMatches:N0} queued source match(es); " +
                $"Mode0 {result.Mode0Sectors:N0}, Mode1 {result.Mode1Sectors:N0}, Mode2 Form1 {result.Mode2Form1Sectors:N0}, Mode2 Form2 {result.Mode2Form2Sectors:N0}, " +
                $"Audio {result.AudioSectors:N0}, Unknown {result.UnknownSectors:N0}.";

            DicProgressBar.Value = 100;
            DicProgressText.Text = resumedWorkingImage ? "Previous DIC recovery resumed" : "DIC skeleton ready";
            AppendDicLog(resumedWorkingImage
                ? $"Resumed cumulative recovery from: {inspection.SkeletonPath}"
                : $"Created synthetic skeleton: {inspection.SkeletonPath}");
            AppendDicLog($"Sector map: Mode0 {result.Mode0Sectors:N0}; Mode1 {result.Mode1Sectors:N0}; Mode2 Form1 {result.Mode2Form1Sectors:N0}; Mode2 Form2 {result.Mode2Form2Sectors:N0}; Audio {result.AudioSectors:N0}; Unknown {result.UnknownSectors:N0}.");
            AppendDicLog($"Filesystem entries: {inspection.Entries.Count:N0}; still requiring source: {filesToRestore:N0}; automatic restore disabled: {disabled:N0}.");
            if (inspection.DicHfsPartitions is { Count: > 0 })
            {
                foreach (DicHfsPartitionInspection hfs in inspection.DicHfsPartitions)
                {
                    AppendDicLog($"HFS INSPECT: partition '{hfs.Name}' ({hfs.Type}) starts at Apple block {hfs.StartBlock:N0} => CD LBA {hfs.PartitionStartLba:N0}+0x{hfs.PartitionStartByteOffset:X3}; MDB expected at LBA {hfs.MasterDirectoryBlockLba:N0}+0x{hfs.MasterDirectoryBlockByteOffset:X3}; bitmap begins at LBA {hfs.VolumeBitmapStartLba:N0}+0x{hfs.VolumeBitmapStartByteOffset:X3}.");
                    if (hfs.MasterDirectoryBlock is DicHfsMasterDirectoryBlock mdb)
                    {
                        string catalogExtents = string.Join(", ", mdb.CatalogExtents.Select(extent => $"{extent.StartBlock}+{extent.BlockCount}"));
                        string extentsExtents = string.Join(", ", mdb.ExtentsOverflowExtents.Select(extent => $"{extent.StartBlock}+{extent.BlockCount}"));
                        AppendDicLog($"HFS MDB: volume '{mdb.VolumeName}'; root files {mdb.FileCountInRoot:N0}; allocation blocks {mdb.AllocationBlockCount:N0} x {mdb.AllocationBlockSize:N0} bytes; first allocation block {mdb.FirstAllocationBlock:N0}; free {mdb.FreeAllocationBlocks:N0}; next CNID {mdb.NextCatalogNodeId:N0}; catalog {mdb.CatalogFileSize:N0} bytes [{catalogExtents}]; extents overflow {mdb.ExtentsOverflowFileSize:N0} bytes [{extentsExtents}].");
                    }
                    else if (hfs.Phase1Synthesized && hfs.SynthesizedMasterDirectoryBlock is DicHfsMasterDirectoryBlock synthesizedMdb)
                    {
                        string catalogExtents = string.Join(", ", synthesizedMdb.CatalogExtents.Select(extent => $"{extent.StartBlock}+{extent.BlockCount}"));
                        string extentsExtents = string.Join(", ", synthesizedMdb.ExtentsOverflowExtents.Select(extent => $"{extent.StartBlock}+{extent.BlockCount}"));
                        AppendDicLog($"HFS PHASE1: provisional MDB synthesized for volume '{synthesizedMdb.VolumeName}'; allocation blocks {synthesizedMdb.AllocationBlockCount:N0} x {synthesizedMdb.AllocationBlockSize:N0} bytes; bitmap used/free {hfs.SynthesizedBitmapUsedBlocks:N0}/{hfs.SynthesizedBitmapFreeBlocks:N0}; catalog scaffold [{catalogExtents}]; extents scaffold [{extentsExtents}].");
                    }
                    else
                    {
                        AppendDicLog("HFS INSPECT: DIC evidence does not contain the MDB payload bytes and phase-1 synthesis could not be applied; the HFS metadata remains zero unless a same-disc donor supplies it.");
                    }
                }
            }
            if (DicVerboseLoggingEnabled)
                AppendDicVerboseInspectionAudit(inspection);
            AppendDicLog("Recovery coverage audit (logical user-data bytes; assumed regions are zero-filled unless a same-disc donor supplies them):");
            foreach (DicRecoveryCoverageItem item in result.CoverageAudit)
            {
                string label = item.Kind switch
                {
                    DicRecoveryCoverageKind.ExactFromDic => "EXACT",
                    DicRecoveryCoverageKind.DeterministicSynthesis => "SYNTH",
                    DicRecoveryCoverageKind.SourcePayload => "SOURCE",
                    DicRecoveryCoverageKind.ProvenBytes => "PROVEN",
                    DicRecoveryCoverageKind.AssumedZero => "ASSUMED ZERO",
                    _ => item.Kind.ToString()
                };
                string lbaRange = item.StartLba is long startLba
                    ? item.EndLba is long endLba && endLba != startLba
                        ? $"; LBA {startLba:N0}-{endLba:N0}"
                        : $"; LBA {startLba:N0}"
                    : string.Empty;
                string donor = item.DonorCapable ? "; donor-capable" : string.Empty;
                AppendDicLog($"COVERAGE [{label}]: {item.Description}; {item.ByteCount:N0} byte(s){lbaRange}{donor}.");
            }

            int optionalExactnessCount = inspection.DonorRequirements?.Count(requirement => !requirement.BlocksResurrection) ?? 0;
            if (optionalExactnessCount > 0)
            {
                AppendDicLog(
                    $"Optional exactness donor regions: {optionalExactnessCount:N0}. These cover unproven system-area bytes, file-sector slack, synthesized/missing metadata, or post-volume sectors where applicable. " +
                    "They do NOT block resurrection; scanning an exact same-disc donor will copy any available regions before file payload restoration.");
            }
            if (associatedSourceCount > 0)
            {
                AppendDicLog($"WARNING: {associatedSourceCount:N0} non-empty ISO9660 Associated File record(s) require a manifest-aware extraction. A normal mounted-filesystem copy may hide these records.");
                AppendDicLog($"Open Other Tools → ISO Extractor, extract the source ISO/BIN, then use its output folder here. The folder must retain {IsoExtractionManifestService.ManifestFileName} and {IsoExtractionManifestService.PrivateDirectoryName}.");
            }
            if (donorRequirementsRequired)
            {
                SkeletonDonorRequirement[] mandatoryRequirements = inspection.DonorRequirements!
                    .Where(requirement => requirement.BlocksResurrection)
                    .ToArray();
                int donorRequirementCount = mandatoryRequirements.Length;
                string donorExamples = string.Join("; ", mandatoryRequirements
                    .Take(4)
                    .Select(requirement => $"{requirement.Path}: {requirement.Reason}"));
                AppendDicLog($"WARNING: {donorRequirementCount:N0} ISO 9660 byte region(s) are not available from ordinary extracted files and require an exact same-disc ISO/BIN donor.");
                if (!string.IsNullOrWhiteSpace(donorExamples))
                    AppendDicLog($"Mandatory donor examples: {donorExamples}");
                AppendDicLog(_dicDonorRequirementsSatisfied
                    ? "Mandatory ISO9660 donor requirement is already satisfied by the resumed/saved working image."
                    : "Resurrection is blocked until a matching donor ISO/BIN is scanned successfully.");
            }
            AppendDicLog($"Persistent recovery state: {_dicStatePath}");
            if (stateLoad.Matches.Count > 0 || _dicAppliedEntries.Count > 0)
                AppendDicLog($"Loaded saved progress: {_dicAppliedEntries.Count:N0} already applied; {queuedMatches:N0} not-yet-applied source match(es); {stateLoad.Matches.Count:N0} source reference(s) retained in state. Total currently satisfied/available: {satisfied:N0}.");
            if (stateLoad.StaleMatches > 0)
                AppendDicLog($"Dropped {stateLoad.StaleMatches:N0} stale saved match(es) because the source file no longer exists or changed size.");

            if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32) ||
                !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) ||
                !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1))
            {
                AppendDicLog(
                    "Original DIC image hashes: " +
                    $"CRC32 {inspection.ExpectedImageCrc32 ?? "n/a"}; " +
                    $"MD5 {inspection.ExpectedImageMd5 ?? "n/a"}; " +
                    $"SHA1 {inspection.ExpectedImageSha1 ?? "n/a"}.");
            }

            foreach (string warning in result.Warnings)
                AppendDicLog("WARNING: " + warning);

            if (associatedSourceCount > 0 && _dicMatches.Values.Count(match => (match.Entry.IsoFileFlags & 0x04) != 0) < associatedSourceCount)
            {
                await ShowMessageAsync(
                    "DumpToolbox — DIC — ISO Extractor required",
                    $"This disc contains {associatedSourceCount:N0} non-empty ISO 9660 Associated File record(s). Most mounted filesystems expose only the normal record when the same pathname is shared, so a normal folder copy is not sufficient for byte-perfect recovery. " +
                    "Open Other Tools → ISO Extractor, extract the source ISO/BIN, then return to DIC and click Match Sources. The extractor output is automatically set as Source Folder. " +
                    $"Do not delete or rename '{IsoExtractionManifestService.ManifestFileName}' or the '{IsoExtractionManifestService.PrivateDirectoryName}' folder; they preserve the hidden record identity. " +
                    "An exact donor-image scan remains available as a fallback, but is no longer required when the extractor folder supplies these records.");
                MainTabControl.SelectedItem = OtherToolsTabItem;
                OtherToolsTabControl.SelectedItem = IsoExtractorTabItem;
            }

            if (donorRequirementsRequired && !_dicDonorRequirementsSatisfied)
            {
                SkeletonDonorRequirement[] mandatoryRequirements = inspection.DonorRequirements!
                    .Where(requirement => requirement.BlocksResurrection)
                    .ToArray();
                int donorRequirementCount = mandatoryRequirements.Length;
                string donorReasons = string.Join("; ", mandatoryRequirements
                    .Take(4)
                    .Select(requirement => $"{requirement.Path}: {requirement.Reason}"));
                await ShowMessageAsync(
                    "DumpToolbox — DIC — donor required",
                    $"This disc contains {donorRequirementCount:N0} ISO 9660 byte region(s) that cannot be reconstructed from ordinary extracted files. " +
                    "This is now limited mainly to Extended Attribute Record blocks or multiple non-associated ISO records that collapse to the same mounted pathname. Associated File payloads can instead be supplied by a manifest-aware folder created with Other Tools → ISO Extractor. " +
                    $"Detected: {donorReasons}. " +
                    "Choose and scan an exact same-disc ISO/BIN donor image before using Resurrect. Only the unavailable physical regions are taken from that donor; normal, Multi-Extent and interleaved file payloads still use ordinary source files.");
            }

            await PersistDicStateAsync(CancellationToken.None);
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendDicLog("DIC load cancelled.");
            DicProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendDicLog($"ERROR: {ex.Message}");
            DicProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — DIC", ex.Message);
        }
        finally
        {
            _dicCts?.Dispose();
            _dicCts = null;
            SetDicRunning(false);
            UpdateDicActionButtons();
        }
    }

}
