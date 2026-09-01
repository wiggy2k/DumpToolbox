using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly AudioRecoveryService _audioRecoveryService = new();
    private readonly ObservableCollection<AudioSourceListItem> _audioSources = new();
    private CancellationTokenSource? _audioRecoveryCts;

    private void InitializeAudioRecoveryTab()
    {
        AudioFilesList.ItemsSource = _audioSources;
    }

    private async void AudioAddButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add audio tracks or raw BIN/ISO sources",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path && AudioRecoveryService.IsSupportedAudioSourcePath(path))
                await AddAudioSourceAsync(path);
        }
        SuggestAudioOutputFolder();
    }

    private async void AudioPlaylistButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose M3U/M3U8/PLS/CUE playlist",
                AllowMultiple = false
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
                return;

            IReadOnlyList<string> sources = AudioRecoveryService.LoadPlaylist(path);
            if (sources.Count == 0)
                throw new InvalidOperationException("No existing supported audio/BIN/ISO source files were found in the playlist.");

            AppendAudioLog($"Playlist: {path}");
            AppendAudioLog($"Supported audio/raw entries found: {sources.Count:N0}");
            foreach (string source in sources)
                await AddAudioSourceAsync(source);
            SuggestAudioOutputFolder();
        }
        catch (Exception ex)
        {
            AppendAudioLog($"ERROR: {ex.Message}");
            await ShowMessageAsync("DumpToolbox — Audio", ex.Message);
        }
    }

    private async Task AddAudioSourceAsync(string path)
    {
        path = Path.GetFullPath(path);
        try
        {
            LosslessAudioInfo info = await _audioRecoveryService.InspectAudioAsync(path);
            var item = new AudioSourceListItem(path, info);
            _audioSources.Add(item);
            string length = info.TotalSamples > 0
                ? $"{info.TotalSamples:N0} sample frames ({info.DurationSeconds:N3}s)" +
                  (info.TotalSamples % AudioRecoveryService.CddaStereoFramesPerSector == 0
                      ? $", {info.TotalSamples / AudioRecoveryService.CddaStereoFramesPerSector:N0} CD sectors"
                      : ", not an exact 588-frame sector multiple")
                : "sample count unavailable from container metadata";
            AppendAudioLog($"{(info.IsCddaCompatible ? "OK" : "REJECT")} {Path.GetFileName(path)} — {info.FormatName}/{info.CodecName} via {info.DecoderName}; " +
                           $"{info.SampleRate:N0} Hz, {info.BitsPerSample}-bit, {info.Channels}ch, {length}");
        }
        catch (Exception ex)
        {
            AppendAudioLog($"REJECT {Path.GetFileName(path)} — {ex.Message}");
            await ShowMessageAsync("DumpToolbox — lossless audio check", $"{Path.GetFileName(path)}\n\n{ex.Message}");
        }
    }

    private void AudioRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = AudioFilesList.SelectedIndex;
        if (index < 0 || index >= _audioSources.Count)
            return;
        _audioSources.RemoveAt(index);
        if (_audioSources.Count > 0)
            AudioFilesList.SelectedIndex = Math.Min(index, _audioSources.Count - 1);
    }

    private void AudioUpButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = AudioFilesList.SelectedIndex;
        if (index <= 0 || index >= _audioSources.Count)
            return;
        _audioSources.Move(index, index - 1);
        AudioFilesList.SelectedIndex = index - 1;
    }

    private void AudioDownButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = AudioFilesList.SelectedIndex;
        if (index < 0 || index >= _audioSources.Count - 1)
            return;
        _audioSources.Move(index, index + 1);
        AudioFilesList.SelectedIndex = index + 1;
    }

    private void AudioClearButton_Click(object? sender, RoutedEventArgs e) => _audioSources.Clear();

    private void AudioHashClearButton_Click(object? sender, RoutedEventArgs e) => AudioTargetsBox.Text = string.Empty;

    private async void AudioOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Audio output folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            AudioOutputFolderBox.Text = path;
    }

    private void SuggestAudioOutputFolder()
    {
        if (_audioSources.Count == 0 || !string.IsNullOrWhiteSpace(AudioOutputFolderBox.Text))
            return;
        string sourceDirectory = Path.GetDirectoryName(_audioSources[0].Path) ?? Directory.GetCurrentDirectory();
        AudioOutputFolderBox.Text = Path.Combine(sourceDirectory, "DumpToolbox_AudioRecovery");
    }

    private async void AudioRecoverButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_audioRecoveryCts is not null)
            return;

        try
        {
            if (_audioSources.Count == 0)
                throw new InvalidOperationException("Add at least one compatible lossless audio file.");
            AudioSourceListItem? invalid = _audioSources.FirstOrDefault(x => !x.Info.IsCddaCompatible);
            if (invalid is not null)
                throw new InvalidOperationException($"{Path.GetFileName(invalid.Path)} is not 44.1 kHz / 16-bit / stereo CDDA audio.");

            RedumpDiscImportResult? redumpImport = null;
            CueSheetAnalysis? redumpCue = null;
            string targetInput = AudioTargetsBox.Text ?? string.Empty;
            if (RedumpDiscImportService.TryParseDiscId(targetInput, out int redumpDiscId))
            {
                SetWindowStatus($"Audio — retrieving Redump disc {redumpDiscId}");
                redumpImport = await RedumpDiscImportService.ImportAsync(redumpDiscId);
                targetInput = redumpImport.TargetText;

                if (!string.IsNullOrWhiteSpace(redumpImport.CuePath))
                {
                    redumpCue = await _cueSheetAnalysisService.AnalyzeAsync(redumpImport.CuePath);
                    if (redumpCue.HasAudio)
                    {
                        var audioFileNames = new HashSet<string>(
                            redumpCue.Tracks.Where(t => t.IsAudio).Select(t => Path.GetFileName(t.FileName)),
                            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

                        string[] audioRows = redumpImport.TargetText
                            .Replace("\r", string.Empty, StringComparison.Ordinal)
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Where(line =>
                            {
                                HashTarget? parsed = TargetParser.Parse(line).FirstOrDefault();
                                string? fileName = parsed?.OutputFileName ?? parsed?.Label;
                                return !string.IsNullOrWhiteSpace(fileName) && audioFileNames.Contains(Path.GetFileName(fileName));
                            })
                            .ToArray();

                        if (audioRows.Length > 0)
                            targetInput = string.Join(Environment.NewLine, audioRows);
                    }
                }

                AudioTargetsBox.Text = targetInput;
            }

            IReadOnlyList<HashTarget> targets = TargetParser.Parse(targetInput);
            foreach (HashTarget target in targets)
            {
                if (target.Size % AudioRecoveryService.CddaSectorBytes != 0)
                    AppendAudioLog($"WARNING: {TargetName(target)} size {target.Size:N0} is not a whole 2352-byte CDDA sector count.");
            }

            string output = AudioOutputFolderBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("Choose an output / working folder.");

            string edgeText = AudioEdgeSecondsBox.Text?.Trim() ?? "0";
            if (!double.TryParse(edgeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double edgeSeconds))
                throw new InvalidOperationException("Edge silence seconds must be a number, for example 5 or 0.5.");

            AudioLogBox.Text = string.Empty;
            UpdateAudioDetachedLog();
            if (redumpImport is not null)
            {
                AppendAudioLog($"REDUMP: imported disc {redumpImport.DiscId} — {redumpImport.DiscTitle}.");
                if (redumpCue is not null)
                {
                    int audioTracks = redumpCue.Tracks.Count(t => t.IsAudio);
                    AppendAudioLog($"REDUMP: CUE imported; selected {targets.Count:N0} audio payload target(s) from {audioTracks:N0} AUDIO track(s).");
                    AppendAudioLog(redumpCue.Description);
                }
                else
                {
                    AppendAudioLog($"REDUMP: CUE information was unavailable; imported all {targets.Count:N0} payload target(s) because audio/data tracks could not be classified automatically.");
                }
            }
            AppendAudioLog("Audio processing starting.");
            AppendAudioLog($"Sources: {_audioSources.Count:N0}");
            AppendAudioLog($"Hash targets: {targets.Count:N0}");
            AppendAudioLog("Lossless audio requirement: exact 44,100 Hz / 16-bit / stereo; no resampling or remixing is performed. BIN/ISO inputs are treated as already-correct raw PCM byte streams and bypass decoding.");
            AppendAudioLog("Working format: raw signed 16-bit stereo Redump BIN PCM, little-endian, no WAV header or processing.");
            AppendAudioLog($"Edge silence search: {edgeSeconds:N3} seconds at each end; FindCRCs alignment: 4 bytes (one stereo sample frame).");
            AppendAudioLog($"Under-dumped edge repair: {(AudioEdgeRepairCheckBox.IsChecked == true ? "enabled (zero-fill first, then Find-ends against combined audio)" : "disabled") }.");
            AppendAudioLog($"Save unmatched first/final edge partials plus outside-zero-trimmed copies: {(AudioSavePartialCheckBox.IsChecked == true ? "enabled" : "disabled") }.");
            bool headsTailsCorpusAvailable = IsAudioHeadsTailsCorpusAvailable(out string? headsTailsCorpusPath);
            bool headsTailsEnabled = headsTailsCorpusAvailable && AudioHeadsTailsCheckBox.IsEnabled && AudioHeadsTailsCheckBox.IsChecked == true && AudioEdgeRepairCheckBox.IsChecked == true;
            AppendAudioLog($"Heads and Tails edge recovery: {(headsTailsEnabled ? $"enabled — source {headsTailsCorpusPath}" : "disabled") }.");

            _audioRecoveryCts = new CancellationTokenSource();
            SetAudioRecoveryRunning(true);
            AudioProgressBar.Value = 0;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<AudioRecoveryProgress>(p =>
            {
                AudioProgressBar.Value = p.Fraction * 100;
                AudioProgressText.Text = $"{p.Stage} — {p.Fraction:P0}";
                SetWindowStatus($"Audio — {p.Message}");
            });

            long lastLogged = -1;
            var searchProgress = new Progress<SearchProgress>(p =>
            {
                double targetFraction = p.TargetCount <= 0 ? 0 : (p.TargetIndex + p.Fraction) / p.TargetCount;
                AudioProgressBar.Value = Math.Clamp(62 + targetFraction * 37, 62, 99);
                AudioProgressText.Text = $"FindCRCs — {p.TargetIndex + 1}/{p.TargetCount}";
                SetWindowStatus($"Audio — {p.Message}");

                if (p.Kind == SearchEventKind.CrcCandidate && p.Offset is long candidate)
                    AppendAudioLog($"CRC candidate: {TargetName(p.Target)} at {candidate:N0} (0x{candidate:X}); verifying MD5...");
                else if (p.Kind == SearchEventKind.Md5Rejected && p.Offset is long rejected)
                    AppendAudioLog($"MD5 rejected: {TargetName(p.Target)} at {rejected:N0} (0x{rejected:X}) actual={p.ActualMd5 ?? "(none)"}");
                else if (p.Kind == SearchEventKind.MatchFound && p.Offset is long matched)
                    AppendAudioLog($"MATCH: {TargetName(p.Target)} at search offset {matched:N0} (0x{matched:X})");
                else if (p.Kind == SearchEventKind.Progress && (lastLogged < 0 || p.BytesScanned - lastLogged >= 128L * 1024 * 1024))
                {
                    AppendAudioLog($"FindCRCs progress: {p.Message}");
                    lastLogged = p.BytesScanned;
                }
            });

            var edgeActivity = new Progress<string>(AppendAudioLog);
            AudioRecoveryResult result = await _audioRecoveryService.RecoverAsync(
                _audioSources.Select(x => x.Path).ToArray(),
                targets,
                output,
                edgeSeconds,
                attemptUnderdumpedEdgeRepair: AudioEdgeRepairCheckBox.IsChecked == true,
                saveEdgePartials: AudioSavePartialCheckBox.IsChecked == true,
                enableHeadsTails: headsTailsEnabled,
                headsTailsSourceFile: headsTailsEnabled ? headsTailsCorpusPath : null,
                progress: progress,
                searchProgress: searchProgress,
                activity: edgeActivity,
                cancellationToken: _audioRecoveryCts.Token);

            stopwatch.Stop();
            AudioProgressBar.Value = 100;
            int matchedCount = result.Tracks.Count(x => x.Found);
            AudioProgressText.Text = $"Complete — {matchedCount}/{result.Tracks.Count} found";
            AppendAudioLog($"Combined decoded CDDA: {result.CombinedBinPath} ({result.CombinedBytes:N0} bytes)");
            foreach (AudioRecoverySource source in result.Sources)
            {
                string sourceMode = source.IsDirectRawSource ? "Direct raw source" : "Converted";
                AppendAudioLog($"{sourceMode}: {source.ConvertedBinPath} — {source.ConvertedBytes:N0} bytes; combined offset {source.CombinedStartOffset:N0}");
            }

            foreach (AudioRecoveredTrack track in result.Tracks)
            {
                if (!track.Found)
                {
                    string partial = string.IsNullOrWhiteSpace(track.OutputPath) ? string.Empty : $"; partial saved: {track.OutputPath}";
                    AppendAudioLog($"NOT FOUND: {TargetName(track.Target)} size={track.Target.Size:N0} CRC32={track.Target.Crc32Hex}{partial}");
                    continue;
                }

                string details = $"RECOVERED: {TargetName(track.Target)} -> {track.OutputPath}; combined offset {track.CombinedOffset:N0}";
                if (track.LeadingSilenceBytes > 0)
                    details += $"; leading silence {track.LeadingSilenceBytes:N0} bytes ({track.LeadingSilenceBytes / 4:N0} sample frames)";
                if (track.TrailingSilenceBytes > 0)
                    details += $"; trailing silence {track.TrailingSilenceBytes:N0} bytes ({track.TrailingSilenceBytes / 4:N0} sample frames)";
                if (track.NearestSourceBoundaryDeltaBytes is long delta)
                    details += $"; nearest source boundary delta {delta:+#;-#;0} bytes ({delta / 4:+#;-#;0} sample frames)";
                AppendAudioLog(details);
            }

            if (AudioDeleteTempCheckBox.IsChecked == true)
            {
                int deleted = CleanupAudioWorkingFiles(result);
                AppendAudioLog($"Working-file cleanup: deleted {deleted:N0} file(s); matched recovered track BINs and saved edge .partial files were kept.");
            }

            AppendAudioLog($"Complete in {stopwatch.Elapsed}. Exact CRC32+MD5 matches: {matchedCount:N0}/{result.Tracks.Count:N0}.");
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendAudioLog("Audio processing cancelled. Temporary .partial/search files were removed; completed converted BINs are retained.");
            AudioProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendAudioLog($"ERROR: {ex.Message}");
            AudioProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Audio", ex.Message);
        }
        finally
        {
            _audioRecoveryCts?.Dispose();
            _audioRecoveryCts = null;
            SetAudioRecoveryRunning(false);
        }
    }

    private void AudioCancelButton_Click(object? sender, RoutedEventArgs e) => _audioRecoveryCts?.Cancel();

    private void SetAudioRecoveryRunning(bool running)
    {
        AudioRecoverButton.IsEnabled = !running;
        AudioCancelButton.IsEnabled = running;
        AudioAddButton.IsEnabled = !running;
        AudioPlaylistButton.IsEnabled = !running;
        AudioRemoveButton.IsEnabled = !running;
        AudioUpButton.IsEnabled = !running;
        AudioDownButton.IsEnabled = !running;
        AudioClearButton.IsEnabled = !running;
        AudioHashClearButton.IsEnabled = !running;
        AudioDeleteTempCheckBox.IsEnabled = !running;
        AudioEdgeRepairCheckBox.IsEnabled = !running;
        AudioSavePartialCheckBox.IsEnabled = !running;
        AudioHeadsTailsCheckBox.IsEnabled = !running;
        AudioOutputBrowseButton.IsEnabled = !running;
        AudioFilesList.IsEnabled = !running;
        AudioOutputFolderBox.IsReadOnly = running;
        AudioEdgeSecondsBox.IsReadOnly = running;
        AudioTargetsBox.IsReadOnly = running;
    }

    private static int CleanupAudioWorkingFiles(AudioRecoveryResult result)
    {
        var keep = new HashSet<string>(
            result.Tracks
                .Where(t => !string.IsNullOrWhiteSpace(t.OutputPath))
                .Select(t => Path.GetFullPath(t.OutputPath!)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var working = new HashSet<string>(
            result.Sources.Where(s => !s.IsDirectRawSource).Select(s => Path.GetFullPath(s.ConvertedBinPath)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        working.Add(Path.GetFullPath(result.CombinedBinPath));

        int deleted = 0;
        foreach (string path in working)
        {
            if (keep.Contains(path))
                continue;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch
            {
                // Cleanup is best-effort; recovery itself has already succeeded.
            }
        }
        return deleted;
    }

    private void AppendAudioLog(string message)
    {
        AppendLog(AudioLogBox, message);
        UpdateAudioDetachedLog();
    }

    private sealed class AudioSourceListItem
    {
        public AudioSourceListItem(string path, LosslessAudioInfo info)
        {
            Path = path;
            Info = info;
        }

        public string Path { get; }
        public LosslessAudioInfo Info { get; }

        public override string ToString()
        {
            string status = Info.IsCddaCompatible ? "✓" : "✗";
            string sectors = Info.TotalSamples <= 0
                ? "unknown length"
                : Info.TotalSamples % AudioRecoveryService.CddaStereoFramesPerSector == 0
                    ? $"{Info.TotalSamples / AudioRecoveryService.CddaStereoFramesPerSector:N0} sectors"
                    : $"{Info.TotalSamples:N0} frames";
            return $"{status} {System.IO.Path.GetFileName(Path),-36}  {Info.FormatName,-14}  {Info.SampleRate,5} Hz  {Info.BitsPerSample,2}-bit  {Info.Channels}ch  {sectors}";
        }
    }
}
