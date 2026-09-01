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
    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose file to search",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            FilePathBox.Text = path;
    }


    private async void FindCrcsCueBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose CUE sheet",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CUE sheet") { Patterns = new[] { "*.cue" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        FindCrcsCueBox.Text = path;
        await RefreshFindCrcsCueAnalysisAsync(showError: true);
    }

    private void FindCrcsCueClearButton_Click(object? sender, RoutedEventArgs e)
    {
        FindCrcsCueBox.Text = string.Empty;
        _findCrcsCueAnalysis = null;
        // Edge repair and partial inspection can still operate in the safe
        // no-CUE two-target singleton case. Only the pregap correction is
        // intrinsically CUE-dependent.
        FindCrcsPregapScrambleCheckBox.IsChecked = false;
        UpdateFindCrcsCueControls();
    }

    private async Task<CueSheetAnalysis?> RefreshFindCrcsCueAnalysisAsync(bool showError)
    {
        string cuePath = FindCrcsCueBox.Text?.Trim() ?? string.Empty;
        if (cuePath.Length == 0)
        {
            _findCrcsCueAnalysis = null;
            UpdateFindCrcsCueControls();
            return null;
        }

        try
        {
            _findCrcsCueAnalysis = await _cueSheetAnalysisService.AnalyzeAsync(cuePath);
            UpdateFindCrcsCueControls();
            return _findCrcsCueAnalysis;
        }
        catch (Exception ex)
        {
            _findCrcsCueAnalysis = null;
            FindCrcsCueStatus.Text = $"CUE error: {ex.Message}";
            FindCrcsEdgeRepairCheckBox.IsChecked = false;
            FindCrcsPregapScrambleCheckBox.IsChecked = false;
            FindCrcsSavePartialCheckBox.IsChecked = false;
            FindCrcsEdgeRepairCheckBox.IsEnabled = false;
            FindCrcsPregapScrambleCheckBox.IsEnabled = false;
            FindCrcsSavePartialCheckBox.IsEnabled = false;
            if (showError)
                await ShowMessageAsync("DumpToolbox — FindCRCs CUE", ex.Message);
            return null;
        }
    }

    private void UpdateFindCrcsCueControls()
    {
        bool idle = _findCrcsCts is null;
        CueSheetAnalysis? cue = _findCrcsCueAnalysis;
        if (cue is null)
        {
            FindCrcsCueStatus.Text = "No CUE selected — two-target singleton edge/silence recovery is still available; Track 02 pregap correction remains CUE-only.";
            FindCrcsEdgeRepairCheckBox.IsEnabled = idle;
            FindCrcsSavePartialCheckBox.IsEnabled = idle;
            FindCrcsPregapScrambleCheckBox.IsChecked = false;
            FindCrcsPregapScrambleCheckBox.IsEnabled = false;
            return;
        }

        FindCrcsCueStatus.Text = cue.Description;
        FindCrcsEdgeRepairCheckBox.IsEnabled = idle && cue.HasAudio;
        FindCrcsSavePartialCheckBox.IsEnabled = idle && cue.HasAudio;
        if (!cue.HasAudio)
        {
            FindCrcsEdgeRepairCheckBox.IsChecked = false;
            FindCrcsSavePartialCheckBox.IsChecked = false;
        }

        CueTrackAnalysis? track2 = cue.FindTrack(2);
        bool canScrambleTrack2 = idle && cue.IsMixedMode &&
                                 track2 is { IsAudio: true, FileBackedPregapFrames: > 0 };
        FindCrcsPregapScrambleCheckBox.IsEnabled = canScrambleTrack2;
        if (!canScrambleTrack2)
            FindCrcsPregapScrambleCheckBox.IsChecked = false;
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_findCrcsCts is not null)
            return;

        try
        {
            string filePath = FilePathBox.Text?.Trim() ?? string.Empty;

            RedumpDiscImportResult? redumpImport = null;
            string targetInput = TargetsBox.Text ?? string.Empty;
            if (RedumpDiscImportService.TryParseDiscId(targetInput, out int redumpDiscId))
            {
                SetWindowStatus($"FindCRCs — retrieving Redump disc {redumpDiscId}");
                redumpImport = await RedumpDiscImportService.ImportAsync(redumpDiscId);
                TargetsBox.Text = redumpImport.TargetText;
                targetInput = redumpImport.TargetText;

                if (!string.IsNullOrWhiteSpace(redumpImport.CuePath))
                {
                    FindCrcsCueBox.Text = redumpImport.CuePath;
                    _findCrcsCueAnalysis = await _cueSheetAnalysisService.AnalyzeAsync(redumpImport.CuePath);
                    UpdateFindCrcsCueControls();
                }
            }

            IReadOnlyList<HashTarget> targets = TargetParser.Parse(targetInput);
            const int alignment = 1;
            bool audioEdgeRepairRequested = FindCrcsEdgeRepairCheckBox.IsChecked == true;
            bool pregapScrambleRequested = FindCrcsPregapScrambleCheckBox.IsChecked == true;
            bool savePartialFilesRequested = FindCrcsSavePartialCheckBox.IsChecked == true;

            string cuePath = FindCrcsCueBox.Text?.Trim() ?? string.Empty;
            CueSheetAnalysis? cue = null;
            if (cuePath.Length > 0)
            {
                cue = await _cueSheetAnalysisService.AnalyzeAsync(cuePath);
                _findCrcsCueAnalysis = cue;
                UpdateFindCrcsCueControls();
            }

            if (pregapScrambleRequested && cue is null)
                throw new InvalidOperationException("Select a valid CUE sheet before using Track 02 pregap scrambling.");
            if (audioEdgeRepairRequested && cue is { HasAudio: false })
                throw new InvalidOperationException("The selected CUE contains no AUDIO tracks, so under-dumped Audio edge repair does not apply.");
            if (pregapScrambleRequested && cue is not null)
            {
                CueTrackAnalysis? track2 = cue.FindTrack(2);
                if (!cue.IsMixedMode || track2 is not { IsAudio: true })
                    throw new InvalidOperationException("Track 02 pregap scrambling applies only to mixed-mode discs where Track 02 is AUDIO.");
                if (track2.FileBackedPregapFrames <= 0)
                    throw new InvalidOperationException("Track 02 has no file-backed INDEX 00 pregap sectors to inspect. A synthetic CUE PREGAP contains no source bytes to scramble.");
            }

            LogBox.Text = string.Empty;
            if (redumpImport is not null)
            {
                AppendFindCrcsLog($"REDUMP: imported disc {redumpImport.DiscId} — {redumpImport.DiscTitle}; {redumpImport.TargetCount:N0} payload target(s).");
                if (!string.IsNullOrWhiteSpace(redumpImport.CuePath))
                    AppendFindCrcsLog($"REDUMP: downloaded CUE information to temporary file: {redumpImport.CuePath}");
                else
                    AppendFindCrcsLog("REDUMP: hash targets imported, but the Redump CUE endpoint was unavailable; continuing without imported CUE information.");
            }
            AppendFindCrcsLog($"Starting scan: {filePath}");
            AppendFindCrcsLog($"Targets: {targets.Count:N0} | Alignment: {alignment:N0} bytes");
            if (cue is not null)
                AppendFindCrcsLog(cue.Description);
            AppendFindCrcsLog("CRC32 candidates will be shown immediately; MD5 is verified only after a CRC hit.");

            _findCrcsCts = new CancellationTokenSource();
            SetFindCrcsRunning(true);

            var stopwatch = Stopwatch.StartNew();
            long lastLoggedBytes = -1;
            var progress = new Progress<SearchProgress>(p =>
            {
                ProgressBar.Value = p.Fraction * 100;
                double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double mibPerSecond = p.BytesScanned / 1048576.0 / seconds;
                ProgressText.Text = $"{p.Fraction:P1}  {mibPerSecond:N1} MiB/s";
                SetWindowStatus($"FindCRCs — {p.Message}");

                if (p.Kind == SearchEventKind.MatchFound && p.Offset is long matchOffset)
                {
                    AppendFindCrcsLog($"*** MATCH FOUND *** {TargetName(p.Target)} size={p.Target.Size:N0} CRC32={p.Target.Crc32Hex} offset={matchOffset:N0} (0x{matchOffset:X})" +
                                      (p.ActualMd5 is null ? "" : $" MD5={p.ActualMd5}"));
                }
                else if (p.Kind == SearchEventKind.CrcCandidate && p.Offset is long candidateOffset)
                {
                    AppendFindCrcsLog($"CRC candidate: {TargetName(p.Target)} size={p.Target.Size:N0} CRC32={p.Target.Crc32Hex} offset={candidateOffset:N0} (0x{candidateOffset:X}) — verifying MD5...");
                }
                else if (p.Kind == SearchEventKind.Md5Rejected && p.Offset is long rejectedOffset)
                {
                    AppendFindCrcsLog($"MD5 rejected: {TargetName(p.Target)} size={p.Target.Size:N0} offset={rejectedOffset:N0} (0x{rejectedOffset:X}) actual={p.ActualMd5 ?? "(none)"}");
                }
                else if (p.Kind == SearchEventKind.Extracted && !string.IsNullOrWhiteSpace(p.OutputPath))
                {
                    AppendFindCrcsLog($"Extracted: {p.OutputPath}");
                }
                else if (p.Kind == SearchEventKind.Progress &&
                         (lastLoggedBytes < 0 || p.BytesScanned - lastLoggedBytes >= 256L * 1024 * 1024))
                {
                    AppendFindCrcsLog($"Progress: {p.Fraction:P1} | {p.BytesScanned / 1048576.0:N0} / {p.SearchableBytes / 1048576.0:N0} MiB | {mibPerSecond:N1} MiB/s | CRC candidates: {p.CrcCandidates:N0}");
                    lastLoggedBytes = p.BytesScanned;
                }
            });

            IReadOnlyList<SearchResult> found = await _findCrcsEngine.SearchAsync(
                filePath, targets, alignment, progress, _findCrcsCts.Token);
            // Keep the ordinary scanner results separate from later synthetic
            // repairs. Pregap-length recovery requires Track 03 to be a real
            // physical source anchor, not a track that was itself reconstructed.
            IReadOnlyList<SearchResult> ordinaryFindCrcsResults = found.ToArray();

            int[] audioTargetIndices;
            bool inferredNoCueSingleton = false;
            if (cue is { HasAudio: true })
            {
                audioTargetIndices = cue.Tracks
                    .Where(t => t.IsAudio)
                    .OrderBy(t => t.Number)
                    .Select(t => ResolveFindCrcsTargetIndex(cue, targets, t.Number))
                    .Distinct()
                    .ToArray();
            }
            else if (cue is null && (audioEdgeRepairRequested || savePartialFilesRequested) &&
                     EdgeRecoveryService.TryInferTwoTargetSingletonCandidate(targets, found, out int inferredIndex, out string inferenceDescription))
            {
                audioTargetIndices = new[] { inferredIndex };
                inferredNoCueSingleton = true;
                AppendFindCrcsLog(
                    $"NO-CUE EDGE: {inferenceDescription} Treating {TargetName(targets[inferredIndex])} as a singleton edge candidate; " +
                    "the repair will proceed only if verified source boundaries establish a safe extent, and any result must match the target hash.");
            }
            else
            {
                audioTargetIndices = Array.Empty<int>();
                if (cue is null && (audioEdgeRepairRequested || savePartialFilesRequested))
                {
                    AppendFindCrcsLog(
                        "NO-CUE EDGE: recovery was requested, but automatic inference requires exactly two targets with exactly one ordinary FindCRCs match. No no-CUE edge repair was attempted.");
                }
            }

            long? mirroredAudioEdgeShiftBytes = pregapScrambleRequested && audioTargetIndices.Length >= 2
                ? GetProvenLastAudioEdgeShiftBytes(filePath, targets, found, audioTargetIndices)
                : null;

            if (mirroredAudioEdgeShiftBytes is > 0)
            {
                AppendFindCrcsLog(
                    $"PREGAP SYMMETRY: positive audio edge shift of {mirroredAudioEdgeShiftBytes.Value:N0} byte(s): the last audio track is proven short by that amount. " +
                    "If ordinary Track 02 pregap scrambling still does not match, Track 02 will test removing the same number of all-zero PCM bytes immediately after the corrected pregap data sector(s).");
            }
            else if (mirroredAudioEdgeShiftBytes is < 0)
            {
                AppendFindCrcsLog(
                    $"PREGAP SYMMETRY: negative audio edge shift of {Math.Abs(mirroredAudioEdgeShiftBytes.Value):N0} byte(s): the last audio edge has that many verified trailing zero byte(s). " +
                    "If ordinary Track 02 pregap scrambling still does not match, Track 02 will test inserting the same number of zero PCM bytes immediately after the corrected pregap data sector(s).");
            }

            if (pregapScrambleRequested && cue is not null)
            {
                CueTrackAnalysis track2 = cue.FindTrack(2)!;
                int track2TargetIndex = ResolveFindCrcsTargetIndex(cue, targets, 2);
                long? cueSuggestedOffset = GetFindCrcsCueSuggestedTrackOffset(cue, track2, filePath);
                AppendFindCrcsLog(
                    $"Track 02 pregap scramble option enabled: correcting empty data sectors within the {track2.FileBackedPregapFrames:N0}-sector physical pregap, then running a 1-byte FindCRCs search for the Track 02 boundary.");
                var scrambleActivity = new Progress<string>(AppendFindCrcsLog);
                PregapScrambleOutcome scramble = await _cdPregapScrambleService.TryRepairTrack2Async(
                    filePath,
                    targets[track2TargetIndex],
                    track2TargetIndex,
                    found,
                    track2.FileBackedPregapFrames,
                    cueSuggestedOffset,
                    mirroredAudioEdgeShiftBytes,
                    scrambleActivity,
                    _findCrcsCts.Token);

                if (scramble.Fixed && scramble.Result is not null)
                {
                    SearchResult[] updated = found.ToArray();
                    updated[track2TargetIndex] = scramble.Result;
                    found = updated;
                }
            }

            // A Track 02 pregap shorter than the normal 00:02:00 can indicate
            // that the tail of Track 01 should be zero-filled. Try this before
            // audio-edge recovery so a newly verified Track 01 can become the
            // strongest possible lower boundary for Track 02.
            if (audioEdgeRepairRequested && cue is { IsMixedMode: true } &&
                cue.FindTrack(1) is { IsAudio: false } &&
                cue.FindTrack(2) is { IsAudio: true } shortPregapTrack2)
            {
                int pregapFrames = GetEffectiveTrack2PregapFrames(shortPregapTrack2);
                if (pregapFrames is > 0 and < 150)
                {
                    int track1TargetIndex = ResolveFindCrcsTargetIndex(cue, targets, 1);
                    string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
                    var pregapActivity = new Progress<string>(AppendFindCrcsLog);
                    SearchResult? repairedTrack1 = await _pregapLengthQuirkRecoveryService.TryRepairTrack1ForShortPregapAsync(
                        filePath,
                        targets[track1TargetIndex],
                        track1TargetIndex,
                        pregapFrames,
                        found[track1TargetIndex],
                        outputDirectory,
                        pregapActivity,
                        _findCrcsCts.Token);
                    if (repairedTrack1 is not null)
                    {
                        SearchResult[] updated = found.ToArray();
                        updated[track1TargetIndex] = repairedTrack1;
                        found = updated;
                    }
                }
            }

            if ((audioEdgeRepairRequested || savePartialFilesRequested) && audioTargetIndices.Length > 0)
            {
                if (audioEdgeRepairRequested)
                {
                    if (inferredNoCueSingleton)
                    {
                        AppendFindCrcsLog(
                            $"Attempting zero-silence edge recovery without a CUE for inferred singleton target {TargetName(targets[audioTargetIndices[0]])}; " +
                            "exact-sized extents will test signed shifts, while short extents will test every zero-padding split between start and end.");
                    }
                    else if (cue is not null && cue.FirstAudioTrackNumber == cue.LastAudioTrackNumber)
                    {
                        AppendFindCrcsLog(
                            $"Attempting Audio edge recovery for the only mapped audio track: Track {cue.FirstAudioTrackNumber:00}; exact-sized extents will test zero-silence shifts in either direction, while short extents will test every missing-silence split between start and end.");
                    }
                    else if (cue is not null)
                    {
                        AppendFindCrcsLog(
                            $"Attempting signed Audio edge recovery for extreme audio tracks Track {cue.FirstAudioTrackNumber:00} and Track {cue.LastAudioTrackNumber:00}, plus internal unmatched AUDIO tracks when both immediate neighbours are hash-matched.");
                    }
                }
                if (savePartialFilesRequested)
                {
                    AppendFindCrcsLog(inferredNoCueSingleton
                        ? "Partial inspection saving is unavailable for the inferred no-CUE singleton because there is no adjacent AUDIO track to anchor it."
                        : "Partial inspection saving enabled: AUDIO partials use only adjacent matched AUDIO tracks as anchors. First and last disc-edge audio partials also get a copy with outside zero-audio frames removed.");
                }

                string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
                var edgeActivity = new Progress<string>(AppendFindCrcsLog);
                EdgeRecoveryOutcome edge = await _edgeRecoveryService.RepairAudioEdgesAsync(
                    filePath, targets, found, audioTargetIndices, outputDirectory,
                    attemptRepair: audioEdgeRepairRequested,
                    savePartialForInspection: savePartialFilesRequested,
                    preferNextAudioAnchorForFirstAudio: cue is { IsMixedMode: true, FirstAudioTrackNumber: 2 } && cue.FindTrack(1) is { IsAudio: false },
                    activity: edgeActivity,
                    cancellationToken: _findCrcsCts.Token);
                found = edge.Results;
            }

            // A Track 02 pregap longer than 00:02:00 can place one or more
            // scrambled final Track 01 data sectors at the beginning of the
            // audio-track hash. This is deliberately attempted only after the
            // ordinary zero-silence edge repair has failed and Track 03 proves
            // that Track 02 is short at its beginning.
            if (audioEdgeRepairRequested && cue is { IsMixedMode: true } &&
                cue.FindTrack(1) is { IsAudio: false } &&
                cue.FindTrack(2) is { IsAudio: true } longPregapTrack2 &&
                cue.FindTrack(3) is not null)
            {
                int pregapFrames = GetEffectiveTrack2PregapFrames(longPregapTrack2);
                if (pregapFrames > 150)
                {
                    int track1TargetIndex = ResolveFindCrcsTargetIndex(cue, targets, 1);
                    int track2TargetIndex = ResolveFindCrcsTargetIndex(cue, targets, 2);
                    int track3TargetIndex = ResolveFindCrcsTargetIndex(cue, targets, 3);
                    string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
                    var pregapActivity = new Progress<string>(AppendFindCrcsLog);
                    SearchResult? repairedTrack2 = await _pregapLengthQuirkRecoveryService.TryRepairTrack2ForLongPregapAsync(
                        filePath,
                        targets[track1TargetIndex],
                        targets[track2TargetIndex],
                        track2TargetIndex,
                        ordinaryFindCrcsResults[track1TargetIndex],
                        pregapFrames,
                        found[track2TargetIndex],
                        ordinaryFindCrcsResults[track3TargetIndex],
                        outputDirectory,
                        pregapActivity,
                        _findCrcsCts.Token);
                    if (repairedTrack2 is not null)
                    {
                        SearchResult[] updated = found.ToArray();
                        updated[track2TargetIndex] = repairedTrack2;
                        found = updated;
                    }
                }
            }

            LogFindCrcsSourceTail(filePath, cue, targets, found);

            stopwatch.Stop();
            ProgressBar.Value = 100;
            int matchCount = found.Count(r => r.Found);
            ProgressText.Text = $"Complete — {matchCount}/{found.Count} found";
            AppendFindCrcsLog($"Scan complete in {stopwatch.Elapsed}. Matches: {matchCount:N0} / {found.Count:N0}.");
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendFindCrcsLog("Scan cancelled.");
            ProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendFindCrcsLog($"ERROR: {ex.Message}");
            ProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — FindCRCs", ex.Message);
        }
        finally
        {
            _findCrcsCts?.Dispose();
            _findCrcsCts = null;
            SetFindCrcsRunning(false);
        }
    }

    private static int GetEffectiveTrack2PregapFrames(CueTrackAnalysis track2) =>
        track2.FileBackedPregapFrames > 0
            ? track2.FileBackedPregapFrames
            : track2.ExplicitPregapFrames ?? 0;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => _findCrcsCts?.Cancel();

    private void TargetsClearButton_Click(object? sender, RoutedEventArgs e)
    {
        TargetsBox.Text = string.Empty;
    }

    /// <summary>
    /// Returns a signed edge shift inferred from the final audio track.
    /// Positive N: the last audio track is N bytes short at its end, so Track 02
    ///             should test removing N bytes from its beginning-side silence.
    /// Negative N: N all-zero bytes exist after the complete last-audio payload,
    ///             so Track 02 should test inserting N zero PCM bytes.
    /// </summary>
    private static long? GetProvenLastAudioEdgeShiftBytes(
        string sourceFile,
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<int> audioTargetIndices)
    {
        if (targets.Count != results.Count || audioTargetIndices.Count < 2 || !File.Exists(sourceFile))
            return null;

        int last = audioTargetIndices[^1];
        int previousAudio = audioTargetIndices[^2];
        if (last < 0 || last >= targets.Count || previousAudio != last - 1)
            return null;
        if (!results[previousAudio].Found || results[previousAudio].Offset is not long previousAudioOffset)
            return null;

        long sourceLength = new FileInfo(sourceFile).Length;
        long expectedStart;
        try
        {
            expectedStart = checked(previousAudioOffset + targets[previousAudio].Size);
        }
        catch (OverflowException)
        {
            return null;
        }

        long? boundaryEnd = null;
        if (last == targets.Count - 1)
            boundaryEnd = sourceLength;
        else if (results[last + 1].Found && results[last + 1].Offset is long followingOffset)
            boundaryEnd = followingOffset;

        if (boundaryEnd is not long end || expectedStart < 0 || end < expectedStart || end > sourceLength)
            return null;

        // If the last target is already matched, any bytes between its verified
        // end and the physical boundary are a candidate opposite-polarity shift
        // only when every one of those bytes is digital silence.
        if (results[last].Found && results[last].Offset is long lastOffset)
        {
            long verifiedEnd;
            try { verifiedEnd = checked(lastOffset + targets[last].Size); }
            catch (OverflowException) { return null; }

            long trailing = end - verifiedEnd;
            if (trailing > 0 && verifiedEnd >= 0 && verifiedEnd <= end &&
                IsFileRangeAllZero(sourceFile, verifiedEnd, trailing))
                return -trailing;

            return null;
        }

        long available = end - expectedStart;
        if (available < 0)
            return null;

        long delta = targets[last].Size - available;
        if (delta > 0)
            return delta; // Proven under-dump at the final edge.

        if (delta < 0)
        {
            long extra = -delta;
            long extraStart;
            try { extraStart = checked(expectedStart + targets[last].Size); }
            catch (OverflowException) { return null; }

            if (extraStart >= expectedStart && extraStart <= end &&
                IsFileRangeAllZero(sourceFile, extraStart, extra))
                return -extra;
        }

        return null;
    }

    private static bool IsFileRangeAllZero(string path, long offset, long length)
    {
        if (length <= 0)
            return true;

        byte[] buffer = new byte[64 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan);
        if (offset < 0 || length > stream.Length - offset)
            return false;

        stream.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int got = stream.Read(buffer, 0, want);
            if (got <= 0)
                return false;
            for (int i = 0; i < got; i++)
                if (buffer[i] != 0)
                    return false;
            remaining -= got;
        }

        return true;
    }

    private void SetFindCrcsRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        BrowseButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        TargetsClearButton.IsEnabled = !running;
        FindCrcsCueBrowseButton.IsEnabled = !running;
        FindCrcsCueClearButton.IsEnabled = !running;
        FindCrcsCueBox.IsReadOnly = running;
        FindCrcsEdgeRepairCheckBox.IsEnabled = false;
        FindCrcsPregapScrambleCheckBox.IsEnabled = false;
        FindCrcsSavePartialCheckBox.IsEnabled = false;
        TargetsBox.IsReadOnly = running;
        FilePathBox.IsReadOnly = running;
        if (!running)
            UpdateFindCrcsCueControls();
    }


    private static int ResolveFindCrcsTargetIndex(
        CueSheetAnalysis cue,
        IReadOnlyList<HashTarget> targets,
        int trackNumber)
    {
        var explicitMatches = new List<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            if (TryGetFindCrcsTargetTrackNumber(targets[i], out int parsed) && parsed == trackNumber)
                explicitMatches.Add(i);
        }

        if (explicitMatches.Count == 1)
            return explicitMatches[0];
        if (explicitMatches.Count > 1)
            throw new InvalidOperationException($"More than one FindCRCs target is labelled as Track {trackNumber:00}; CUE-aware repair cannot choose safely.");

        int cuePosition = -1;
        for (int i = 0; i < cue.Tracks.Count; i++)
        {
            if (cue.Tracks[i].Number == trackNumber)
            {
                cuePosition = i;
                break;
            }
        }

        if (cuePosition >= 0 && targets.Count == cue.Tracks.Count)
            return cuePosition;

        throw new InvalidOperationException(
            $"Could not map CUE Track {trackNumber:00} to a FindCRCs hash target. " +
            "Paste targets for every CUE track in track order, or use Redump rows/filenames that include the track number.");
    }

    private static bool TryGetFindCrcsTargetTrackNumber(HashTarget target, out int trackNumber)
    {
        foreach (string? text in new[] { target.Label, target.OutputFileName })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            Match match = FindCrcsTargetTrackNumberRegex.Match(text);
            if (match.Success && int.TryParse(match.Groups["number"].Value, out trackNumber))
                return true;
        }

        trackNumber = 0;
        return false;
    }

    private static long? GetFindCrcsCueSuggestedTrackOffset(
        CueSheetAnalysis cue,
        CueTrackAnalysis track,
        string sourceFile)
    {
        if (!track.FileType.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
            return null;

        int firstStoredFrame = track.Index00Frames ?? track.Index01Frames;
        if (firstStoredFrame < 0)
            return null;

        string source = Path.GetFullPath(sourceFile);
        string cueDirectory = Path.GetDirectoryName(cue.CuePath) ?? Directory.GetCurrentDirectory();
        string referenced = string.IsNullOrWhiteSpace(track.FileName)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(cueDirectory, track.FileName));

        bool exactReferencedFile = referenced.Length > 0 &&
            source.Equals(referenced, StringComparison.OrdinalIgnoreCase);
        int distinctCueFiles = cue.Tracks
            .Select(t => t.FileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        bool singleFileCue = distinctCueFiles == 1;

        if (!exactReferencedFile && !singleFileCue)
            return null;

        long offset = checked((long)firstStoredFrame * Iso2BinService.RawSectorSize);
        long length = new FileInfo(source).Length;
        return offset <= length ? offset : null;
    }

    private void LogFindCrcsSourceTail(
        string sourceFile,
        CueSheetAnalysis? cue,
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results)
    {
        if (cue is null || cue.Tracks.Count == 0 || targets.Count == 0 || results.Count != targets.Count)
            return;

        var orderedTracks = new List<(int TrackNumber, int TargetIndex)>();
        try
        {
            foreach (CueTrackAnalysis track in cue.Tracks.OrderBy(t => t.Number))
                orderedTracks.Add((track.Number, ResolveFindCrcsTargetIndex(cue, targets, track.Number)));
        }
        catch (InvalidOperationException ex)
        {
            AppendFindCrcsLog($"SOURCE TAIL: cannot determine the final track extent from the CUE/targets: {ex.Message}");
            return;
        }

        if (orderedTracks.Count == 0)
            return;

        long sourceLength = new FileInfo(sourceFile).Length;
        int finalPosition = orderedTracks.Count - 1;
        (int finalTrackNumber, int finalTargetIndex) = orderedTracks[finalPosition];

        long? expectedEnd = null;
        string? basis = null;

        SearchResult finalResult = results[finalTargetIndex];
        if (finalResult.Found && finalResult.Offset is long finalOffset)
        {
            try
            {
                expectedEnd = checked(finalOffset + targets[finalTargetIndex].Size);
                basis = $"matched Track {finalTrackNumber:00}";
            }
            catch (OverflowException)
            {
                return;
            }
        }
        else
        {
            for (int position = finalPosition - 1; position >= 0; position--)
            {
                int anchorTargetIndex = orderedTracks[position].TargetIndex;
                SearchResult anchor = results[anchorTargetIndex];
                if (!anchor.Found || anchor.Offset is not long anchorOffset)
                    continue;

                try
                {
                    long projectedEnd = anchorOffset;
                    for (int i = position; i <= finalPosition; i++)
                        projectedEnd = checked(projectedEnd + targets[orderedTracks[i].TargetIndex].Size);

                    expectedEnd = projectedEnd;
                    basis = $"projected from matched Track {orderedTracks[position].TrackNumber:00} using the expected sizes of the remaining track(s)";
                }
                catch (OverflowException)
                {
                    return;
                }

                break;
            }
        }

        if (expectedEnd is not long discEnd || basis is null)
        {
            AppendFindCrcsLog(
                $"SOURCE TAIL: the expected end of Track {finalTrackNumber:00} cannot be established because neither the final track nor an earlier track provides a reliable matched anchor.");
            return;
        }

        long delta = sourceLength - discEnd;
        if (delta > 0)
        {
            long sectors = delta / Iso2BinService.RawSectorSize;
            long remainder = delta % Iso2BinService.RawSectorSize;
            string sectorDescription = remainder == 0
                ? $"{sectors:N0} raw 2352-byte sector(s)"
                : $"{sectors:N0} complete raw 2352-byte sector(s) + {remainder:N0} byte(s)";

            AppendFindCrcsLog(
                $"SOURCE TAIL: source image continues {delta:N0} byte(s) past the expected end of Track {finalTrackNumber:00} at source offset {discEnd:N0} (0x{discEnd:X}); " +
                $"that is {sectorDescription}. Extent was {basis}.");
        }
        else if (delta == 0)
        {
            AppendFindCrcsLog(
                $"SOURCE TAIL: source image ends exactly at the expected end of Track {finalTrackNumber:00} ({discEnd:N0} bytes); extent was {basis}.");
        }
        else
        {
            AppendFindCrcsLog(
                $"SOURCE TAIL: source image ends {Math.Abs(delta):N0} byte(s) before the expected end of Track {finalTrackNumber:00}; extent was {basis}.");
        }
    }

    private static string TargetName(HashTarget target) =>
        string.IsNullOrWhiteSpace(target.Label) ? "target" : target.Label;

    private void AppendFindCrcsLog(string message) => AppendLog(LogBox, message);

    // Concatenate tab
}
