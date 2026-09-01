using System.Text;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly StringBuilder _discEvidenceLog = new();

    private void InitializeDiscEvidenceDevTools()
    {
        IniSettingsStore? settings = _userSettings;
        if (settings is null)
        {
            DevEvidenceTabItem.IsVisible = false;
            return;
        }

        bool enabled = settings.GetBool("General", "devtools", false) || settings.GetBool("Settings", "devtools", false);
        DevEvidenceTabItem.IsVisible = enabled;
        if (!enabled) return;
        DevEvidenceThreadsBox.Value = Math.Clamp(settings.GetInt("DevTools", "EvidenceThreads", Math.Min(4, Environment.ProcessorCount)), 1, 64);
        DevEvidenceDatabaseText.Text = $"Evidence DB: {_discEvidenceService.DatabasePath}";
        _ = RefreshDiscEvidenceStatusAsync();
    }

    private async Task RefreshDiscEvidenceStatusAsync()
    {
        try
        {
            var s = await Task.Run(() => _discEvidenceService.GetQueueStatsAsync());
            DevEvidenceStatusText.Text = $"Pending catalogue units: {s.Pending:N0}; evidence units retained: {s.Complete:N0}";
        }
        catch (Exception ex) { DevEvidenceStatusText.Text = $"Evidence database error: {ex.Message}"; }
    }

    private void AppendDiscEvidenceLog(string message)
        => AppendDiscEvidenceLogBatch(new[] { message });

    private void AppendDiscEvidenceLogBatch(IReadOnlyList<string> messages)
    {
        string text = UiLogText.AppendTimestamped(_discEvidenceLog, messages);
        DevEvidenceLogTextBox.Text = text;
        DevEvidenceLogTextBox.CaretIndex = text.Length;
    }

    private async void DevEvidenceScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_discEvidenceCts is not null) return;
        _discEvidenceCts = new CancellationTokenSource();
        int workers = Math.Clamp((int)(DevEvidenceThreadsBox.Value ?? 1), 1, 64);
        _userSettings?.Set("DevTools", "EvidenceThreads", workers); _userSettings?.Save();
        DevEvidenceScanButton.IsEnabled = false; DevEvidenceCancelButton.IsEnabled = true; DevEvidenceAnalyseButton.IsEnabled = false; DevEvidenceResetButton.IsEnabled = false;
        DevEvidenceProgressBar.Value = 0;
        UiBatchedLogProgress? log = null;
        try
        {
            using var progress = new UiLatestProgress<DiscEvidenceProgress>(p =>
            {
                DevEvidenceProgressBar.Value = p.Total == 0 ? 100 : p.Completed * 100.0 / p.Total;
                DevEvidenceStatusText.Text = $"{p.Phase}: {p.Completed:N0}/{p.Total:N0} units; {p.Images:N0} image(s); {p.Errors:N0} error(s)";
            });
            log = new UiBatchedLogProgress(AppendDiscEvidenceLogBatch);
            await Task.Run(
                () => _discEvidenceService.ScanPendingAsync(workers, progress, log, _discEvidenceCts.Token),
                _discEvidenceCts.Token);
            log.Flush();
        }
        catch (OperationCanceledException) { log?.Flush(); AppendDiscEvidenceLog("CANCELLED: completed evidence was retained; unfinished units remain pending."); }
        catch (Exception ex) { log?.Flush(); AppendDiscEvidenceLog($"FATAL: {ex.GetType().Name}: {ex.Message}"); await ShowMessageAsync("DumpToolbox — Disc Evidence", ex.Message); }
        finally
        {
            log?.Dispose();
            _discEvidenceCts.Dispose(); _discEvidenceCts = null;
            DevEvidenceScanButton.IsEnabled = true; DevEvidenceCancelButton.IsEnabled = false; DevEvidenceAnalyseButton.IsEnabled = true; DevEvidenceResetButton.IsEnabled = true;
            await RefreshDiscEvidenceStatusAsync();
        }
    }

    private void DevEvidenceCancelButton_Click(object? sender, RoutedEventArgs e) => _discEvidenceCts?.Cancel();

    private async void DevEvidenceAnalyseButton_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose evidence analysis output folder", AllowMultiple = false });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath(); if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            using var log = new UiBatchedLogProgress(AppendDiscEvidenceLogBatch);
            await Task.Run(() => _discEvidenceService.AnalyseAsync(path, log));
            log.Flush();
        }
        catch (Exception ex) { await ShowMessageAsync("DumpToolbox — Disc Evidence", ex.Message); }
    }

    private async void DevEvidenceResetButton_Click(object? sender, RoutedEventArgs e)
    {
        await Task.Run(() => _skeletoolCatalogueService.ResetEvidenceGatheredAsync());
        AppendDiscEvidenceLog("All present SHA-1 catalogue units marked pending for evidence gathering. Existing evidence database rows were retained and will be refreshed on rescan.");
        await RefreshDiscEvidenceStatusAsync();
    }

    private void DevEvidenceClearLogButton_Click(object? sender, RoutedEventArgs e) { _discEvidenceLog.Clear(); DevEvidenceLogTextBox.Text = string.Empty; }
}
