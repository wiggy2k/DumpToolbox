using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly StringBuilder _audioHeadsTailsLogText = new();

    private string? GetConfiguredAudioHeadsTailsCorpusPath()
    {
        string configured = _userSettings?.Get("HeadsAndTails", "CorpusPath", string.Empty)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try { return Path.GetFullPath(configured); }
        catch { return null; }
    }

    private bool IsAudioHeadsTailsCorpusAvailable(out string? corpusPath)
    {
        corpusPath = GetConfiguredAudioHeadsTailsCorpusPath();
        return !string.IsNullOrWhiteSpace(corpusPath) && File.Exists(corpusPath);
    }

    private void AppendAudioHeadsTailsLog(string message)
        => AppendAudioHeadsTailsLogBatch(new[] { message });

    private void AppendAudioHeadsTailsLogBatch(IReadOnlyList<string> messages)
    {
        string text = UiLogText.AppendTimestamped(_audioHeadsTailsLogText, messages);
        SettingsHeadsTailsLogTextBox.Text = text;
        SettingsHeadsTailsLogTextBox.CaretIndex = text.Length;
    }

    private void SettingsHeadsTailsLogClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _audioHeadsTailsLogText.Clear();
        SettingsHeadsTailsLogTextBox.Text = string.Empty;
    }

    private async Task RefreshAudioHeadsTailsRootsAsync()
    {
        try
        {
            IReadOnlyList<AudioHeadsTailsRoot> roots = await Task.Run(() => _audioHeadsTailsCatalogueService.GetRootsAsync());
            bool corpusAvailable = IsAudioHeadsTailsCorpusAvailable(out string? configuredCorpusPath);
            string corpusDisplay = string.IsNullOrWhiteSpace(configuredCorpusPath)
                ? "not configured in DumpToolbox.ini"
                : configuredCorpusPath + (File.Exists(configuredCorpusPath) ? string.Empty : " (missing)");
            SettingsHeadsTailsDatabasePathText.Text = $"Database: {_audioHeadsTailsCatalogueService.DatabasePath} (fixed beside DumpToolbox.exe)\nCorpus: {corpusDisplay}";
            if (!SettingsHeadsTailsCorpusPathTextBox.IsFocused)
                SettingsHeadsTailsCorpusPathTextBox.Text = configuredCorpusPath ?? string.Empty;
            SettingsHeadsTailsRootsPanel.Children.Clear();

            // No registered collections means there is nothing to manage or search, so keep
            // the Audio Recovery option hidden.  With collections present, expose the option
            // but only allow it to be enabled when the INI points at an existing corpus file.
            AudioHeadsTailsCheckBox.IsVisible = roots.Count > 0;
            AudioHeadsTailsCheckBox.IsEnabled = roots.Count > 0 && corpusAvailable;
            if (!corpusAvailable)
                AudioHeadsTailsCheckBox.IsChecked = false;

            if (roots.Count == 0)
            {
                AudioHeadsTailsCheckBox.IsChecked = false;
                SettingsHeadsTailsRootsPanel.Children.Add(new TextBlock { Text = "No collection folders have been added yet.", Opacity = 0.7 });
                return;
            }

            if (!corpusAvailable)
            {
                SettingsHeadsTailsRootsPanel.Children.Add(new TextBlock
                {
                    Text = "Heads and Tails mode is unavailable until a corpus output path is configured and that AudioHeadsandTails.bin file has been built. Choose a path above, then run Check for changes.",
                    Opacity = 0.75,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 0, 0, 8)
                });
            }

            foreach (AudioHeadsTailsRoot root in roots)
            {
                var path = new TextBlock { Text = root.Path, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                string scanned = root.LastSuccessfulScanUtc is DateTimeOffset last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Never";
                var status = new TextBlock { Text = root.LastError is { Length: > 0 } ? $"Last successful scan: {scanned}   |   Last error: {root.LastError}" : $"Last scanned: {scanned}", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var scan = new Button { Content = "Check for changes", Padding = new Avalonia.Thickness(12, 6), Margin = new Avalonia.Thickness(0, 0, 6, 0) };
                scan.Click += async (_, _) => await RunAudioHeadsTailsScanAsync(new[] { root.Id });
                var remove = new Button { Content = "Remove folder", Padding = new Avalonia.Thickness(12, 6) };
                remove.Click += async (_, _) =>
                {
                    await Task.Run(() => _audioHeadsTailsCatalogueService.DeactivateRootAsync(root.Id));
                    AppendAudioHeadsTailsLog($"Collection removed from catalogue: {root.Path}. The Heads and Tails corpus is append-only, so bytes already written by this collection are not rewritten or deleted.");
                    await RefreshAudioHeadsTailsRootsAsync();
                };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(scan); buttons.Children.Add(remove);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Avalonia.Thickness(0,2) };
                Grid.SetColumn(path,0); Grid.SetRow(path,0); Grid.SetColumn(status,0); Grid.SetRow(status,1); Grid.SetColumn(buttons,1); Grid.SetRow(buttons,0); Grid.SetRowSpan(buttons,2);
                row.Children.Add(path); row.Children.Add(status); row.Children.Add(buttons);
                SettingsHeadsTailsRootsPanel.Children.Add(new Border { Padding = new Avalonia.Thickness(10), CornerRadius = new Avalonia.CornerRadius(4), Child = row });
            }
        }
        catch (Exception ex)
        {
            SettingsHeadsTailsStatusText.Text = $"Could not read Heads and Tails catalogue: {ex.Message}";
            AudioHeadsTailsCheckBox.IsVisible = false;
        }
    }


    private async void SettingsHeadsTailsCorpusBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose Heads and Tails corpus file",
            SuggestedFileName = "AudioHeadsandTails.bin",
            DefaultExtension = "bin",
            FileTypeChoices = new[] { new FilePickerFileType("BIN file") { Patterns = new[] { "*.bin" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        SettingsHeadsTailsCorpusPathTextBox.Text = path;
        IniSettingsStore? settings = _userSettings;
        if (settings is not null)
        {
            settings.Set("HeadsAndTails", "CorpusPath", Path.GetFullPath(path));
            settings.Save();
        }
        await RefreshAudioHeadsTailsRootsAsync();
    }

    private string RequireConfiguredHeadsTailsCorpusPath()
    {
        string raw = SettingsHeadsTailsCorpusPathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Choose the AudioHeadsandTails.bin output path before scanning.");
        string full = Path.GetFullPath(raw);
        IniSettingsStore? settings = _userSettings;
        if (settings is not null)
        {
            settings.Set("HeadsAndTails", "CorpusPath", full);
            settings.Save();
        }
        return full;
    }

    private async void SettingsHeadsTailsAddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add audio disc image collection folder", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        try
        {
            long id = await Task.Run(() => _audioHeadsTailsCatalogueService.AddRootAsync(path));
            await RefreshAudioHeadsTailsRootsAsync();
            await RunAudioHeadsTailsScanAsync(new[] { id });
        }
        catch (Exception ex) { await ShowMessageAsync("DumpToolbox — Heads and Tails", ex.Message); }
    }

    private async void SettingsHeadsTailsCheckAllButton_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<AudioHeadsTailsRoot> roots = await Task.Run(() => _audioHeadsTailsCatalogueService.GetRootsAsync());
        await RunAudioHeadsTailsScanAsync(roots.Select(r => r.Id).ToArray());
    }

    private void SettingsHeadsTailsCancelButton_Click(object? sender, RoutedEventArgs e) => _audioHeadsTailsCts?.Cancel();

    private async Task RunAudioHeadsTailsScanAsync(IReadOnlyList<long> rootIds)
    {
        if (_audioHeadsTailsCts is not null || rootIds.Count == 0) return;
        string corpusPath;
        try { corpusPath = RequireConfiguredHeadsTailsCorpusPath(); }
        catch (Exception ex) { await ShowMessageAsync("DumpToolbox — Heads and Tails", ex.Message); return; }

        int workers = Math.Clamp((int)(SettingsHeadsTailsThreadsBox.Value ?? 4), 1, 64);
        _userSettings?.Set("HeadsAndTails", "Threads", workers);
        _userSettings?.Save();

        _audioHeadsTailsCts = new CancellationTokenSource();
        SettingsHeadsTailsCheckAllButton.IsEnabled = false;
        SettingsHeadsTailsRootsPanel.IsEnabled = false;
        SettingsHeadsTailsThreadsBox.IsEnabled = false;
        SettingsHeadsTailsCancelButton.IsEnabled = true;
        SettingsHeadsTailsProgressBar.Value = 0;
        AudioHeadsTailsCorpusWriterSession? corpusSession = null;
        UiBatchedLogProgress? logProgress = null;
        try
        {
            AppendAudioHeadsTailsLog($"=== Heads and Tails collection scan started ({rootIds.Count} folder(s), {workers} thread(s)) ===");
            AppendAudioHeadsTailsLog($"Corpus output: {corpusPath}");
            logProgress = new UiBatchedLogProgress(AppendAudioHeadsTailsLogBatch);

            corpusSession = await Task.Run(
                () => _audioHeadsTailsCatalogueService.BeginCorpusScanAsync(
                    corpusPath, rootIds, logProgress, _audioHeadsTailsCts.Token),
                _audioHeadsTailsCts.Token);
            AudioHeadsTailsCorpusWriterSession activeCorpus = corpusSession;
            AppendAudioHeadsTailsLog("CORPUS: append-only output is open; only new/changed sources will append bytes. Unchanged sources never rewrite their existing heads/tails.");

            for (int rootIndex = 0; rootIndex < rootIds.Count; rootIndex++)
            {
                int ordinal = rootIndex;
                using var progress = new UiLatestProgress<AudioHeadsTailsProgress>(p =>
                {
                    double inside = p.SourcesTotal <= 0 ? 0 : (double)p.SourcesProcessed / p.SourcesTotal;
                    SettingsHeadsTailsProgressBar.Value = ((ordinal + inside) / rootIds.Count) * 100.0;
                    SettingsHeadsTailsStatusText.Text = $"{p.SourcesProcessed:N0}/{p.SourcesTotal:N0} sources; {p.TracksExtracted:N0} audio track(s); {p.AllZeroTracks:N0} all-zero; {p.SourcesSkipped:N0} unchanged; {p.SourcesErrored:N0} error(s); corpus {activeCorpus.BytesWritten:N0} bytes";
                });
                AppendAudioHeadsTailsLog($"Starting collection {rootIndex + 1}/{rootIds.Count}; enumeration and archive processing are running in the background.");
                await Task.Run(
                    () => _audioHeadsTailsCatalogueService.ScanRootAsync(
                        rootIds[rootIndex], workers, progress, logProgress, _audioHeadsTailsCts.Token, activeCorpus),
                    _audioHeadsTailsCts.Token);
            }

            AppendAudioHeadsTailsLog("CORPUS: all scan workers finished; draining the writer queue and flushing the configured file.");
            await Task.Run(() => activeCorpus.CompleteAsync(_audioHeadsTailsCts.Token), _audioHeadsTailsCts.Token);
            logProgress.Flush();
            SettingsHeadsTailsProgressBar.Value = 100;
            if (File.Exists(corpusPath))
            {
                long bytes = new FileInfo(corpusPath).Length;
                SettingsHeadsTailsStatusText.Text = $"Scan complete; AudioHeadsandTails.bin contains {bytes:N0} bytes.";
                AppendAudioHeadsTailsLog($"=== Scan complete; streamed Heads and Tails corpus: {corpusPath} ({bytes:N0} bytes) ===");
            }
            else
            {
                SettingsHeadsTailsStatusText.Text = "Scan complete, but the configured corpus file was not produced; Heads and Tails mode remains unavailable.";
                AppendAudioHeadsTailsLog("WARNING: scan completed but the configured Heads and Tails corpus file does not exist; Audio Recovery mode remains unavailable.");
            }
        }
        catch (OperationCanceledException)
        {
            logProgress?.Flush();
            SettingsHeadsTailsStatusText.Text = "Scan cancelled.";
            AppendAudioHeadsTailsLog("CANCELLED: already-appended corpus bytes and committed processing metadata are retained. SQLite stores no audio bytes, so no corpus rebuild is performed.");
            if (corpusSession is not null)
            {
                try { await Task.Run(() => corpusSession.CompleteAsync()); } catch { }
                AudioHeadsTailsCorpusWriterSession cancelledSession = corpusSession;
                await Task.Run(async () => await cancelledSession.DisposeAsync());
                corpusSession = null;
            }
        }
        catch (Exception ex)
        {
            logProgress?.Flush();
            SettingsHeadsTailsStatusText.Text = "Scan failed.";
            AppendAudioHeadsTailsLog($"FATAL: {ex.Message}");
            await ShowMessageAsync("DumpToolbox — Heads and Tails", ex.Message);
        }
        finally
        {
            logProgress?.Dispose();
            if (corpusSession is not null)
            {
                AudioHeadsTailsCorpusWriterSession completedSession = corpusSession;
                try { await Task.Run(async () => await completedSession.DisposeAsync()); } catch (Exception ex) { AppendAudioHeadsTailsLog($"WARNING: corpus writer close failed: {ex.Message}"); }
            }
            _audioHeadsTailsCts?.Dispose();
            _audioHeadsTailsCts = null;
            SettingsHeadsTailsCheckAllButton.IsEnabled = true;
            SettingsHeadsTailsRootsPanel.IsEnabled = true;
            SettingsHeadsTailsThreadsBox.IsEnabled = true;
            SettingsHeadsTailsCancelButton.IsEnabled = false;
            await RefreshAudioHeadsTailsRootsAsync();
        }
    }
}
