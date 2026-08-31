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
    private async void DicResurrectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dicCts is not null || _dicInspection is null)
            return;

        SkeletonInspectionResult inspection = _dicInspection;
        try
        {
            int donorRequirementCount = inspection.DonorRequirements?.Count(requirement => requirement.BlocksResurrection) ?? 0;
            if (donorRequirementCount > 0 && !_dicDonorRequirementsSatisfied)
            {
                await ShowMessageAsync(
                    "DumpToolbox — DIC — donor required",
                    $"Resurrection cannot continue because {donorRequirementCount:N0} on-disc ISO 9660 byte region(s) are still unavailable from ordinary extracted files. " +
                    "Scan an exact same-disc ISO/BIN donor first; only those unavailable regions will be restored from it.");
                AppendDicLog($"ERROR: Resurrect blocked: {donorRequirementCount:N0} ISO 9660 payload region(s) require an exact same-disc ISO/BIN donor.");
                return;
            }

            string output = DicOutputBox.Text?.Trim() ?? string.Empty;
            bool allowMissing = DicAllowMissingCheckBox.IsChecked == true;
            DicProgressBar.Value = 0;
            DicProgressText.Text = "Preparing...";
            AppendDicLog($"Rebuilding DIC image to: {output}");
            AppendDicLog(allowMissing
                ? "Partial DIC recovery enabled: newly found payloads will be committed to the cumulative working BIN and remaining payloads stay skeletonized."
                : "Complete DIC recovery required: all not-yet-applied payloads must be available before this rebuild.");

            _dicCts = new CancellationTokenSource();
            SetDicRunning(true);

            // Reapply supplementary/Joliet metadata from all persisted/current Joliet
            // source matches before the payload pass.
            DicJolietNameUpdateResult jolietUpdate = await _dicLogImportService.ApplyMatchedJolietNamesAsync(
                inspection,
                _dicMatches,
                DicSourceFolderBox.Text?.Trim() ?? string.Empty,
                _dicCts.Token);
            if (jolietUpdate.Updated)
                AppendDicLog($"Joliet metadata prepared from matched source names before resurrection: {jolietUpdate.SourcePathsUsed:N0} source pathname(s).");
            foreach (string warning in jolietUpdate.Warnings)
                AppendDicLog("JOLIET: " + warning);

            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<SkeletonResurrectionProgress>(p =>
            {
                DicProgressBar.Value = p.Fraction * 100;
                DicProgressText.Text = $"{p.Fraction:P0}  {p.Message}";
                SetWindowStatus($"DIC — {p.Message}");

                if (!string.IsNullOrWhiteSpace(p.EntryPath) &&
                    _dicNodes.TryGetValue(p.EntryPath, out SkeletonTreeNode? node))
                {
                    if (p.Kind == SkeletonResurrectionEventKind.RestoringEntry)
                        node.Status = "…";
                    else if (p.Kind == SkeletonResurrectionEventKind.EntryRestored)
                        node.Status = "✓R";
                }
            });
            var activity = new Progress<string>(AppendDicLog);

            Dictionary<string, SkeletonSourceMatch> activeMatches = _dicMatches
                .Where(pair => !_dicAppliedEntries.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            string[] newlyApplied = activeMatches.Keys.ToArray();
            SkeletonResurrectionResult result = await _skeletonService.ResurrectAsync(
                inspection,
                activeMatches,
                output,
                allowMissing,
                progress,
                activity,
                _dicCts.Token,
                ResolveEofSlackAmbiguityAsync);

            foreach (string path in newlyApplied)
                _dicAppliedEntries.Add(path);

            if (_dicState is not null)
                _dicState.LastOutputPath = result.OutputPath;

            // The new output becomes the cumulative base image. Entries already
            // committed to it no longer require their original source file on future
            // sessions/folder scans.
            IReadOnlyList<SkeletonContentEntry> adjustedEntries = inspection.Entries
                .Select(entry => _dicAppliedEntries.Contains(entry.Path)
                    ? entry with { RequiresSource = false }
                    : entry)
                .ToArray();
            _dicInspection = inspection with { SkeletonPath = result.OutputPath, Entries = adjustedEntries };

            // Keep old source references in the state as a fallback in case the
            // cumulative BIN is later moved/deleted. Future rebuilds ignore matches
            // whose entries are already present in the working BIN.
            await PersistDicStateAsync(_dicCts.Token);

            stopwatch.Stop();
            MarkDicMissingStatuses();
            DicProgressBar.Value = 100;
            DicProgressText.Text = $"Complete — {_dicAppliedEntries.Count:N0} cumulatively applied";
            AppendDicLog($"Complete in {stopwatch.Elapsed}. This pass restored/satisfied {result.RestoredEntries:N0} entries; {result.MissingEntries:N0} remain missing.");
            AppendDicLog($"Cumulative working BIN: {result.OutputPath} ({result.OutputBytes:N0} bytes). {_dicAppliedEntries.Count:N0} file payload(s) are now permanently carried forward by that image.");

            if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32) ||
                !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) ||
                !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1))
            {
                AppendDicLog(result.MissingEntries == 0
                    ? "DIC VERIFY: rebuild completed with all required payloads present; calculating the original whole-image hashes."
                    : $"DIC VERIFY: rebuild completed with {result.MissingEntries:N0} payload(s) still reported missing; calculating whole-image hashes anyway because the DIC hashes are the final exactness authority.");
                var verifyOptions = new HashCalculationOptions(
                    Crc32: !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32),
                    Md5: !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5),
                    Sha1: !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1));
                HashCalculationResult imageHashes = await _hashCalculationService.CalculateAsync(
                    result.OutputPath,
                    verifyOptions,
                    cancellationToken: _dicCts.Token);

                bool allHashesMatch = true;
                if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32) && imageHashes.Hashes.TryGetValue("CRC32", out string? actualCrc32))
                {
                    bool match = actualCrc32.Equals(inspection.ExpectedImageCrc32, StringComparison.OrdinalIgnoreCase);
                    allHashesMatch &= match;
                    AppendDicLog($"DIC VERIFY: CRC32 {(match ? "MATCH" : "DIFFERS")} — expected {inspection.ExpectedImageCrc32}, actual {actualCrc32}.");
                }
                if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) && imageHashes.Hashes.TryGetValue("MD5", out string? actualMd5))
                {
                    bool match = actualMd5.Equals(inspection.ExpectedImageMd5, StringComparison.OrdinalIgnoreCase);
                    allHashesMatch &= match;
                    AppendDicLog($"DIC VERIFY: MD5 {(match ? "MATCH" : "DIFFERS")} — expected {inspection.ExpectedImageMd5}, actual {actualMd5}.");
                }
                if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1) && imageHashes.Hashes.TryGetValue("SHA-1", out string? actualSha1))
                {
                    bool match = actualSha1.Equals(inspection.ExpectedImageSha1, StringComparison.OrdinalIgnoreCase);
                    allHashesMatch &= match;
                    AppendDicLog($"DIC VERIFY: SHA1 {(match ? "MATCH" : "DIFFERS")} — expected {inspection.ExpectedImageSha1}, actual {actualSha1}.");
                }

                if (allHashesMatch)
                {
                    AppendDicLog("DIC VERIFY: BYTE-EXACT whole-image match confirmed.");
                }
                else
                {
                    long assumedBytes = _dicCoverageAudit
                        .Where(item => item.Kind == DicRecoveryCoverageKind.AssumedZero)
                        .Sum(item => item.ByteCount);
                    AppendDicLog(assumedBytes > 0
                        ? $"DIC VERIFY: whole-image hash differs. The coverage audit still contains {assumedBytes:N0} zero-assumed/unproven user-data byte(s); an exact same-disc donor may resolve those regions."
                        : "DIC VERIFY: whole-image hash differs even though the coverage audit has no remaining zero-assumed user-data bytes; inspect source matches and sector metadata.");
                }
            }

            DicOutputBox.Text = _skeletonService.SuggestOutputPath(_dicInspection);
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendDicLog("DIC rebuild cancelled. Partial output removed; saved recovery state was not advanced.");
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

    private async void DicClearStateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dicCts is not null || string.IsNullOrWhiteSpace(_dicStatePath))
            return;

        try
        {
            _dicRecoveryStateService.Clear(_dicStatePath);
            _dicMatches.Clear();
            _dicAppliedEntries.Clear();
            if (_dicState is not null)
            {
                _dicState.Matches.Clear();
                _dicState.AppliedEntries.Clear();
                _dicState.LastOutputPath = null;
                _dicState.LastDonorImagePath = null;
                _dicState.DonorRequirementsSatisfied = false;
            }
            _dicDonorRequirementsSatisfied = false;
            DicDonorImageBox.Text = string.Empty;
            ResetDicMatchStatuses();
            MarkDicMissingStatuses();
            AppendDicLog("Persistent DIC recovery state cleared. Reload the DIC logs to regenerate a pristine synthetic skeleton if the current session had already resumed from a cumulative rebuilt BIN.");
            DicProgressText.Text = "Saved matches cleared";
            UpdateDicActionButtons();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("DumpToolbox — DIC", ex.Message);
        }
    }

    private void DicCancelButton_Click(object? sender, RoutedEventArgs e) => _dicCts?.Cancel();

    private int CountQueuedDicMatches()
    {
        if (_dicInspection is null)
            return 0;

        var requiredPaths = _dicInspection.Entries
            .Where(IsRequiredDicSourceEntry)
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _dicMatches.Keys.Count(path =>
            requiredPaths.Contains(path) && !_dicAppliedEntries.Contains(path));
    }

    private int MergeDicMatches(IReadOnlyDictionary<string, SkeletonSourceMatch> newMatches)
    {
        int added = 0;
        foreach ((string path, SkeletonSourceMatch match) in newMatches)
        {
            if (!_dicMatches.TryGetValue(path, out SkeletonSourceMatch? existing))
            {
                // Applied entries no longer need payload data, but retaining their
                // source identity is still useful for rebuilding otherwise-unlogged
                // Joliet metadata after a restart.
                _dicMatches[path] = match;
                bool requiresPayload = _dicInspection?.Entries.Any(entry =>
                    entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    IsRequiredDicSourceEntry(entry)) == true;
                if (requiresPayload && !_dicAppliedEntries.Contains(path))
                    added++;
                continue;
            }

            bool incomingHasJolietIdentity = match.MatchMethod.Contains("Joliet", StringComparison.OrdinalIgnoreCase);
            bool existingHasJolietIdentity = existing.MatchMethod.Contains("Joliet", StringComparison.OrdinalIgnoreCase);
            if (incomingHasJolietIdentity && !existingHasJolietIdentity)
                _dicMatches[path] = match;
        }
        return added;
    }

    private async Task PersistDicStateAsync(CancellationToken cancellationToken)
    {
        if (_dicState is null || string.IsNullOrWhiteSpace(_dicStatePath))
            return;

        _dicState.DonorRequirementsSatisfied = _dicDonorRequirementsSatisfied;
        await _dicRecoveryStateService.SaveAsync(
            _dicStatePath,
            _dicState,
            _dicMatches,
            _dicAppliedEntries,
            cancellationToken);
    }

}
