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
            var s = await _discEvidenceService.GetQueueStatsAsync();
            DevEvidenceStatusText.Text = $"Pending catalogue units: {s.Pending:N0}; evidence units retained: {s.Complete:N0}";
        }
        catch (Exception ex) { DevEvidenceStatusText.Text = $"Evidence database error: {ex.Message}"; }
    }

    private void AppendDiscEvidenceLog(string message)
    {
        _discEvidenceLog.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);
        DevEvidenceLogTextBox.Text = _discEvidenceLog.ToString();
        DevEvidenceLogTextBox.CaretIndex = DevEvidenceLogTextBox.Text?.Length ?? 0;
    }

    private async void DevEvidenceScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_discEvidenceCts is not null) return;
        _discEvidenceCts = new CancellationTokenSource();
        int workers = Math.Clamp((int)(DevEvidenceThreadsBox.Value ?? 1), 1, 64);
        _userSettings?.Set("DevTools", "EvidenceThreads", workers); _userSettings?.Save();
        DevEvidenceScanButton.IsEnabled = false; DevEvidenceCancelButton.IsEnabled = true; DevEvidenceAnalyseButton.IsEnabled = false; DevEvidenceResetButton.IsEnabled = false;
        DevEvidenceProgressBar.Value = 0;
        try
        {
            var progress = new Progress<DiscEvidenceProgress>(p =>
            {
                DevEvidenceProgressBar.Value = p.Total == 0 ? 100 : p.Completed * 100.0 / p.Total;
                DevEvidenceStatusText.Text = $"{p.Phase}: {p.Completed:N0}/{p.Total:N0} units; {p.Images:N0} image(s); {p.Errors:N0} error(s)";
            });
            var log = new Progress<string>(AppendDiscEvidenceLog);
            await _discEvidenceService.ScanPendingAsync(workers, progress, log, _discEvidenceCts.Token);
        }
        catch (OperationCanceledException) { AppendDiscEvidenceLog("CANCELLED: completed evidence was retained; unfinished units remain pending."); }
        catch (Exception ex) { AppendDiscEvidenceLog($"FATAL: {ex.GetType().Name}: {ex.Message}"); await ShowMessageAsync("DumpToolbox — Disc Evidence", ex.Message); }
        finally
        {
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
        try { await _discEvidenceService.AnalyseAsync(path, new Progress<string>(AppendDiscEvidenceLog)); }
        catch (Exception ex) { await ShowMessageAsync("DumpToolbox — Disc Evidence", ex.Message); }
    }

    private async void DevEvidenceResetButton_Click(object? sender, RoutedEventArgs e)
    {
        await _skeletoolCatalogueService.ResetEvidenceGatheredAsync();
        AppendDiscEvidenceLog("All present SHA-1 catalogue units marked pending for evidence gathering. Existing evidence database rows were retained and will be refreshed on rescan.");
        await RefreshDiscEvidenceStatusAsync();
    }

    private void DevEvidenceClearLogButton_Click(object? sender, RoutedEventArgs e) { _discEvidenceLog.Clear(); DevEvidenceLogTextBox.Text = string.Empty; }
}
