using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly StringBuilder _sha1CatalogueLogText = new();
    private Window? _sha1CatalogueLogWindow;
    private TextBox? _sha1CatalogueLogWindowBox;
    private bool _sha1CatalogueLogUndocked;

    private void AppendSha1CatalogueLog(string message)
        => AppendSha1CatalogueLogBatch(new[] { message });

    private void AppendSha1CatalogueLogBatch(IReadOnlyList<string> messages)
    {
        string text = UiLogText.AppendTimestamped(_sha1CatalogueLogText, messages);
        SettingsSha1LogTextBox.Text = text;
        SettingsSha1LogTextBox.CaretIndex = text.Length;
        if (_sha1CatalogueLogWindowBox is not null)
        {
            _sha1CatalogueLogWindowBox.Text = text;
            ScrollDetachedLogToEnd(_sha1CatalogueLogWindowBox);
        }
    }

    private void SettingsSha1LogClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _sha1CatalogueLogText.Clear();
        SettingsSha1LogTextBox.Text = string.Empty;
        if (_sha1CatalogueLogWindowBox is not null) _sha1CatalogueLogWindowBox.Text = string.Empty;
    }

    private void SettingsSha1LogUndockButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_sha1CatalogueLogUndocked)
        {
            _sha1CatalogueLogWindow?.Activate();
            return;
        }
        _sha1CatalogueLogUndocked = true;
        SettingsSha1LogTextBox.IsVisible = false;
        SettingsSha1CatalogueGrid.RowDefinitions[7].Height = new GridLength(0);
        SettingsSha1LogUndockButton.Content = "Show log";
        ShowSha1CatalogueLogWindow();
    }

    private void ShowSha1CatalogueLogWindow()
    {
        if (_sha1CatalogueLogWindow is not null)
        {
            _sha1CatalogueLogWindow.Activate();
            return;
        }
        (_sha1CatalogueLogWindow, _sha1CatalogueLogWindowBox) = CreateActivityLogWindow(
            "DumpToolbox — SHA-1 Database Scan Log",
            _sha1CatalogueLogText.ToString(),
            DockSha1CatalogueLog,
            () =>
            {
                _sha1CatalogueLogWindow = null;
                _sha1CatalogueLogWindowBox = null;
                if (_sha1CatalogueLogUndocked)
                {
                    _sha1CatalogueLogUndocked = false;
                    SettingsSha1LogTextBox.IsVisible = true;
                    SettingsSha1CatalogueGrid.RowDefinitions[7].Height = new GridLength(1, GridUnitType.Star);
                    SettingsSha1LogUndockButton.Content = "Undock";
                }
            });
        _sha1CatalogueLogWindow?.Show(this);
    }

    private void DockSha1CatalogueLog()
    {
        _sha1CatalogueLogUndocked = false;
        SettingsSha1LogTextBox.IsVisible = true;
        SettingsSha1CatalogueGrid.RowDefinitions[7].Height = new GridLength(1, GridUnitType.Star);
        SettingsSha1LogUndockButton.Content = "Undock";
        _sha1CatalogueLogWindow?.Close();
    }
    private async Task RefreshSha1CatalogueRootsAsync()
    {
        try
        {
            SettingsSha1DatabasePathText.Text = $"Database: {_skeletoolCatalogueService.DatabasePath}";
            IReadOnlyList<SkeletoolCatalogueRoot> roots = await Task.Run(() => _skeletoolCatalogueService.GetRootsAsync());
            SettingsSha1RootsPanel.Children.Clear();
            if (roots.Count == 0)
            {
                SettingsSha1RootsPanel.Children.Add(new TextBlock
                {
                    Text = "No collection folders have been added yet.",
                    Opacity = 0.7
                });
                return;
            }

            foreach (SkeletoolCatalogueRoot root in roots)
            {
                var path = new TextBlock
                {
                    Text = root.Path,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                string scanned = root.LastSuccessfulScanUtc is DateTimeOffset last
                    ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "Never";
                var status = new TextBlock
                {
                    Text = root.LastError is { Length: > 0 }
                        ? $"Last successful scan: {scanned}   |   Last error: {root.LastError}"
                        : $"Last scanned: {scanned}",
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };
                var scan = new Button { Content = "Check for changes", Padding = new Avalonia.Thickness(12, 6), Margin = new Avalonia.Thickness(0, 0, 6, 0) };
                scan.Click += async (_, _) => await RunSha1CatalogueScanAsync(new[] { root.Id });
                var remove = new Button { Content = "Remove folder", Padding = new Avalonia.Thickness(12, 6) };
                remove.Click += async (_, _) =>
                {
                    await Task.Run(() => _skeletoolCatalogueService.DeactivateRootAsync(root.Id));
                    await RefreshSha1CatalogueRootsAsync();
                };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
                buttons.Children.Add(scan);
                buttons.Children.Add(remove);
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    RowDefinitions = new RowDefinitions("Auto,Auto"),
                    Margin = new Avalonia.Thickness(0, 2)
                };
                Grid.SetColumn(path, 0); Grid.SetRow(path, 0);
                Grid.SetColumn(status, 0); Grid.SetRow(status, 1);
                Grid.SetColumn(buttons, 1); Grid.SetRow(buttons, 0); Grid.SetRowSpan(buttons, 2);
                row.Children.Add(path); row.Children.Add(status); row.Children.Add(buttons);
                var border = new Border { Padding = new Avalonia.Thickness(10), CornerRadius = new Avalonia.CornerRadius(4), Child = row };
                SettingsSha1RootsPanel.Children.Add(border);
            }
        }
        catch (Exception ex)
        {
            SettingsSha1StatusText.Text = $"Could not read SHA-1 catalogue: {ex.Message}";
        }
    }

    private async void SettingsSha1AddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add CD/DVD image collection folder",
            AllowMultiple = false
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        try
        {
            long id = await Task.Run(() => _skeletoolCatalogueService.AddRootAsync(path));
            await RefreshSha1CatalogueRootsAsync();
            await RunSha1CatalogueScanAsync(new[] { id });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("DumpToolbox — SHA-1 Database", ex.Message);
        }
    }

    private async void SettingsSha1CheckAllButton_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<SkeletoolCatalogueRoot> roots = await Task.Run(() => _skeletoolCatalogueService.GetRootsAsync());
        await RunSha1CatalogueScanAsync(roots.Select(r => r.Id).ToArray());
    }

    private void SettingsSha1CancelButton_Click(object? sender, RoutedEventArgs e) => _sha1CatalogueCts?.Cancel();

    private async Task RunSha1CatalogueScanAsync(IReadOnlyList<long> rootIds)
    {
        if (_sha1CatalogueCts is not null || rootIds.Count == 0) return;
        _sha1CatalogueCts = new CancellationTokenSource();
        SettingsSha1CheckAllButton.IsEnabled = false;
        SettingsSha1RootsPanel.IsEnabled = false;
        SettingsSha1CancelButton.IsEnabled = true;
        SettingsSha1ProgressBar.Value = 0;
        UiBatchedLogProgress? activityLog = null;
        try
        {
            AppendSha1CatalogueLog($"=== Collection scan started ({rootIds.Count} folder(s), {Math.Clamp((int)(SettingsSha1ThreadsBox.Value ?? 1), 1, 64)} thread(s)) ===");
            activityLog = new UiBatchedLogProgress(AppendSha1CatalogueLogBatch);
            for (int rootIndex = 0; rootIndex < rootIds.Count; rootIndex++)
            {
                int rootOrdinal = rootIndex;
                using var progress = new UiLatestProgress<SkeletoolCatalogueProgress>(p =>
                {
                    double inside = p.SourcesTotal <= 0 ? 0 : (double)p.SourcesProcessed / p.SourcesTotal;
                    SettingsSha1ProgressBar.Value = ((rootOrdinal + inside) / rootIds.Count) * 100.0;
                    SettingsSha1StatusText.Text = $"{p.SourcesProcessed:N0} / {p.SourcesTotal:N0} scanned";
                });
                int workers = Math.Clamp((int)(SettingsSha1ThreadsBox.Value ?? 1), 1, 64);
                await Task.Run(
                    () => _skeletoolCatalogueService.ScanRootAsync(
                        rootIds[rootIndex], progress, workers, activityLog, _sha1CatalogueCts.Token),
                    _sha1CatalogueCts.Token);
            }
            activityLog.Flush();
            SettingsSha1ProgressBar.Value = 100;
            SettingsSha1StatusText.Text = "Scan complete.";
            AppendSha1CatalogueLog("=== Collection scan complete ===");
            if (_skeletonInspection is not null && IsSha1DatabaseEnabled)
            {
                IReadOnlyDictionary<string, SkeletonSourceMatch> catalogueMatches =
                    await Task.Run(
                        () => _skeletoolCatalogueService.FindMatchesAsync(_skeletonInspection, _sha1CatalogueCts.Token),
                        _sha1CatalogueCts.Token);
                _skeletonMatches = MergeSkeletonMatches(_skeletonMatches, catalogueMatches);
                MarkSkeletonMissingStatuses();
            }
        }
        catch (OperationCanceledException)
        {
            activityLog?.Flush();
            SettingsSha1StatusText.Text = "Scan cancelled.";
            AppendSha1CatalogueLog("CANCELLED: collection scan cancelled; completed source records were retained.");
        }
        catch (Exception ex)
        {
            activityLog?.Flush();
            SettingsSha1StatusText.Text = $"Collection scan error: {ex.Message}";
            AppendSha1CatalogueLog($"FATAL: {ex.GetType().Name}: {ex.Message}");
            await ShowMessageAsync("DumpToolbox — SHA-1 Database", ex.Message);
        }
        finally
        {
            activityLog?.Dispose();
            _sha1CatalogueCts.Dispose();
            _sha1CatalogueCts = null;
            SettingsSha1CheckAllButton.IsEnabled = true;
            SettingsSha1RootsPanel.IsEnabled = true;
            SettingsSha1CancelButton.IsEnabled = false;
            await RefreshSha1CatalogueRootsAsync();
        }
    }
}
