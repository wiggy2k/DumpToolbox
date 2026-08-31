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
    private readonly ObservableCollection<SkeletonTreeNode> _dicTreeRoots = new();
    private readonly Dictionary<string, SkeletonTreeNode> _dicNodes = new(StringComparer.OrdinalIgnoreCase);

    private SkeletonInspectionResult? _dicInspection;
    private Dictionary<string, SkeletonSourceMatch> _dicMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dicAppliedEntries = new(StringComparer.OrdinalIgnoreCase);
    private DicLogSet? _dicLogs;
    private DicRecoveryState? _dicState;
    private string? _dicStatePath;
    private bool _dicDonorRequirementsSatisfied;
    private IReadOnlyList<DicRecoveryCoverageItem> _dicCoverageAudit = Array.Empty<DicRecoveryCoverageItem>();
    private CancellationTokenSource? _dicCts;

    // DIC log production is decoupled from Avalonia. Recovery work only enqueues text;
    // a dedicated background consumer coalesces bursts and streams batches to the UI.
    private readonly ConcurrentQueue<string> _dicLogQueue = new();
    private readonly SemaphoreSlim _dicLogSignal = new(0);
    private int _dicLogSignalPending;
    private readonly CancellationTokenSource _dicLogPumpCts = new();
    private readonly StringBuilder _dicLogText = new();
    private Task? _dicLogPumpTask;

    private bool DicVerboseLoggingEnabled => DicVerboseLoggingCheckBox.IsChecked == true;

    private void InitializeDicTab()
    {
        DicTree.ItemsSource = _dicTreeRoots;
        _dicLogPumpTask ??= Task.Run(() => DicLogPumpAsync(_dicLogPumpCts.Token));
    }

    private async void DicLogBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder containing DiscImageCreator logs",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            DicLogPathBox.Text = path;
    }

    private async void DicSourceBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder containing recovered source files",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            DicSourceFolderBox.Text = path;
    }

    private async void DicDonorBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose 2048-byte ISO or 2352-byte BIN donor image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CD image") { Patterns = new[] { "*.iso", "*.bin", "*.img" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            DicDonorImageBox.Text = path;
    }

    private async void DicOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = _dicInspection is null
            ? "DIC_rebuilt.bin"
            : Path.GetFileName(_skeletonService.SuggestOutputPath(_dicInspection));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose rebuilt DIC output image",
            SuggestedFileName = suggested
        });

        if (file?.TryGetLocalPath() is { } path)
            DicOutputBox.Text = path;
    }

}
