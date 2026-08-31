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
    private async void DicMatchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dicCts is not null || _dicInspection is null)
            return;

        SkeletonInspectionResult inspection = _dicInspection;
        try
        {
            string folder = DicSourceFolderBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder))
            {
                await ShowMessageAsync("DumpToolbox — DIC", "Choose a source folder first.");
                return;
            }

            IsoExtractionManifest? extractorManifest = IsoExtractionManifestService.TryLoad(folder);
            if (extractorManifest is not null)
            {
                if (IsoExtractionManifestService.MatchesInspection(extractorManifest, inspection, out string manifestReason))
                {
                    AppendDicLog($"DumpToolbox ISO Extractor manifest detected: volume '{extractorManifest.VolumeIdentifier}', {extractorManifest.Files.Count:N0} ISO record(s). Exact DIC PVD identity verified; extractor record geometry may be used.");
                    if (DicVerboseLoggingEnabled)
                        AppendDicVerboseManifestAudit(inspection, folder, extractorManifest, payloadOnly: false);
                }
                else if (IsoExtractionManifestService.IsPayloadOnlyCompatible(extractorManifest, inspection, out string payloadReason))
                {
                    AppendDicLog($"WARNING: DumpToolbox ISO Extractor PVD identity was not verified ({manifestReason}). Using manifest in PAYLOAD-ONLY mode because {payloadReason}. Extractor LBAs/extents will NOT be trusted; private files are eligible only when DIC ISO path + exact size + flags identify one record uniquely.");
                    if (DicVerboseLoggingEnabled)
                        AppendDicVerboseManifestAudit(inspection, folder, extractorManifest, payloadOnly: true);
                }
                else
                    AppendDicLog($"WARNING: DumpToolbox ISO Extractor manifest was found but is not compatible with this DIC disc: {manifestReason}. Private extractor records will not be used.");
            }
            else if (inspection.Entries.Any(entry => (entry.IsoFileFlags & 0x04) != 0 && IsRequiredDicSourceEntry(entry)))
            {
                AppendDicLog($"WARNING: this DIC recovery contains Associated File records but '{IsoExtractionManifestService.ManifestFileName}' is not present in the selected source folder. Normal files will still be matched, but Associated records will remain missing.");
            }

            DicProgressBar.Value = 0;
            DicProgressText.Text = "Matching...";
            AppendDicLog($"Scanning additional source folder: {folder} (all subfolders; primary ISO9660 paths are matched exactly first, then a conservative Joliet-name projection is tried)");
            AppendDicLog("New matches are merged with saved progress rather than replacing matches found in previous folders/sessions.");

            _dicCts = new CancellationTokenSource();
            SetDicRunning(true);
            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<SkeletonSourceScanProgress>(p =>
            {
                DicProgressBar.Value = p.Fraction * 100;
                DicProgressText.Text = $"{p.FilesProcessed:N0}/{p.FilesTotal:N0} filesystem entries";
                SetWindowStatus($"DIC — matching {p.FilesProcessed}/{p.FilesTotal}");
            });

            IReadOnlyDictionary<string, SkeletonSourceMatch> found = await _skeletonService.MatchSourcesAsync(
                inspection,
                folder,
                recursive: true,
                forceRehash: false,
                progress: progress,
                cancellationToken: _dicCts.Token);

            int added = MergeDicMatches(found);

            DicJolietNameUpdateResult jolietUpdate = await _dicLogImportService.ApplyMatchedJolietNamesAsync(
                inspection,
                _dicMatches,
                folder,
                _dicCts.Token);
            if (jolietUpdate.Updated)
                AppendDicLog($"Joliet metadata updated from matched source names: {jolietUpdate.SourcePathsUsed:N0} source pathname(s), {jolietUpdate.DicLongAliasesUsed:N0} DIC long-name alias(es).");
            foreach (string warning in jolietUpdate.Warnings)
                AppendDicLog("JOLIET: " + warning);

            await PersistDicStateAsync(_dicCts.Token);

            stopwatch.Stop();
            MarkDicMissingStatuses();
            int required = inspection.Entries.Count(IsRequiredDicSourceEntry);
            DicProgressBar.Value = 100;
            DicProgressText.Text = $"{CountQueuedDicMatches():N0} queued + {_dicAppliedEntries.Count:N0} applied";
            AppendDicLog($"Folder scan complete in {stopwatch.Elapsed}. Added {added:N0} new match(es); {CountQueuedDicMatches():N0} payload(s) are queued from saved/current sources and {_dicAppliedEntries.Count:N0} are already present in the working BIN. {required:N0} still require source in this working image.");

            foreach (IGrouping<string, SkeletonSourceMatch> group in found.Values
                         .GroupBy(m => m.MatchMethod)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendDicLog($"This scan — {group.Key}: {group.Count():N0}");
            }

            if (DicVerboseLoggingEnabled)
                AppendDicVerboseMatchAudit(inspection, found);

            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendDicLog("Source matching cancelled.");
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

    private async void DicDonorScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dicCts is not null || _dicInspection is null || _dicLogs is null)
            return;

        string donorPath = DicDonorImageBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(donorPath))
        {
            await ShowMessageAsync("DumpToolbox — DIC", "Choose a donor ISO/BIN first.");
            return;
        }

        try
        {
            _dicCts = new CancellationTokenSource();
            SetDicRunning(true);
            DicProgressBar.Value = 0;
            DicProgressText.Text = "Reading donor filesystem...";
            AppendDicLog($"Scanning donor image: {donorPath}");

            string cacheRoot = Path.Combine(_dicLogs.Directory, ".dumptoolbox_dic_donor_cache");
            var progress = new Progress<DicDonorProgress>(p =>
            {
                DicProgressBar.Value = p.Fraction * 100;
                DicProgressText.Text = p.Message;
                SetWindowStatus($"DIC — {p.Message}");
            });

            DicDonorScanResult donor = await _dicDonorImageService.MatchAsync(
                _dicInspection,
                donorPath,
                cacheRoot,
                applySameDiscMetadata: true,
                progress: progress,
                cancellationToken: _dicCts.Token);

            int added = MergeDicMatches(donor.Matches);
            bool donorRequired = _dicInspection.DonorRequirements?.Any(requirement => requirement.BlocksResurrection) == true;
            _dicDonorRequirementsSatisfied = !donorRequired || donor.DonorRequirementsSatisfied;
            if (_dicState is not null)
            {
                _dicState.LastDonorImagePath = donor.ImagePath;
                _dicState.DonorRequirementsSatisfied = _dicDonorRequirementsSatisfied;
            }
            DicDonorImageBox.Text = donor.ImagePath;

            DicJolietNameUpdateResult jolietUpdate = await _dicLogImportService.ApplyMatchedJolietNamesAsync(
                _dicInspection,
                _dicMatches,
                _dicLogs.Directory,
                _dicCts.Token);
            if (jolietUpdate.Updated)
                AppendDicLog($"Joliet metadata updated from donor-image pathname evidence: {jolietUpdate.SourcePathsUsed:N0} source pathname(s), {jolietUpdate.DicLongAliasesUsed:N0} DIC long-name alias(es).");
            foreach (string warning in jolietUpdate.Warnings)
                AppendDicLog("JOLIET: " + warning);

            await PersistDicStateAsync(_dicCts.Token);
            MarkDicMissingStatuses();

            AppendDicLog(
                $"Donor identified as {donor.SectorSize}-byte sectors; volume '{donor.VolumeIdentifier}'; " +
                $"primary ISO9660 records {donor.Files.Count:N0}.");
            if (donor.HasJoliet)
                AppendDicLog("Donor contains a supplementary/Joliet descriptor; its pathname tree is used only as validated name/casing evidence and is never copied as donor metadata.");
            AppendDicLog(
                $"Identity check: PVD {(donor.PvdMatches ? "MATCH" : "DIFFERS")}; volume label {(donor.VolumeIdentifierMatches ? "MATCH" : "DIFFERS")}.");

            if (donor.SameDisc)
            {
                AppendDicLog($"Same-disc donor accepted. Applied {donor.MetadataSectorsApplied:N0} original primary ISO9660 metadata sector(s) to the working image. Donor files are matched only by exact ISO9660 relative path, filename and byte length (case-insensitive).");
                if (donor.RequiredPayloadsApplied > 0)
                    AppendDicLog($"Mandatory donor requirement satisfied: copied {donor.RequiredPayloadsApplied:N0} required ISO 9660 payload region(s) directly from the donor image.");
                if (donor.OptionalExactnessRegionsApplied > 0)
                    AppendDicLog($"Optional exactness recovery: copied {donor.OptionalExactnessRegionsApplied:N0} assumed/unproven region(s) directly from the same-disc donor.");
            }
            else
            {
                AppendDicLog("Donor is not an exact DIC PVD+volume-label match, so none of its filesystem metadata was copied. Its primary ISO9660 filesystem was searched as a source of candidate payloads; any unambiguously mapped Joliet pathnames are used only as name/casing evidence for those matched primary records.");
            }

            AppendDicLog($"Donor scan added {added:N0} new persistent source match(es); donor-extracted payloads are cached under: {cacheRoot}");
            foreach (IGrouping<string, SkeletonSourceMatch> group in donor.Matches.Values
                         .GroupBy(m => m.MatchMethod)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                AppendDicLog($"Donor matches by {group.Key}: {group.Count():N0}");
            foreach (string warning in donor.Warnings)
                AppendDicLog("DONOR: " + warning);

            DicProgressBar.Value = 100;
            DicProgressText.Text = $"Donor: {donor.Matches.Count:N0} found, {added:N0} new";
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendDicLog("Donor scan cancelled.");
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
