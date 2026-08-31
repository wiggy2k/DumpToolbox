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
    private readonly HashSearchEngine _findCrcsEngine = new();
    private readonly CueSheetAnalysisService _cueSheetAnalysisService = new();
    private readonly CdPregapScrambleService _cdPregapScrambleService = new();
    private readonly PregapLengthQuirkRecoveryService _pregapLengthQuirkRecoveryService = new();
    private readonly EdgeRecoveryService _edgeRecoveryService = new();
    private readonly ConcatenateService _concatenateService = new();
    private readonly Iso2BinService _iso2BinService = new();
    private readonly SkeletonResurrectionService _skeletonService = new();
    private readonly Ps3IrdRebuildService _irdService = new();
    private readonly SkeletoolCatalogueService _skeletoolCatalogueService = new();
    private readonly AudioHeadsTailsCatalogueService _audioHeadsTailsCatalogueService = new();
    private readonly DiscEvidenceService _discEvidenceService;
    private readonly DicLogImportService _dicLogImportService = new();
    private readonly DicDonorImageService _dicDonorImageService = new();
    private readonly DicRecoveryStateService _dicRecoveryStateService = new();
    private readonly ObservableCollection<string> _concatenateFiles = new();
    private readonly ObservableCollection<SkeletonTreeNode> _skeletonTreeRoots = new();
    private readonly Dictionary<string, SkeletonTreeNode> _skeletonNodes = new(StringComparer.OrdinalIgnoreCase);

    private SkeletonInspectionResult? _skeletonInspection;
    private IReadOnlyDictionary<string, SkeletonSourceMatch> _skeletonMatches =
        new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _findCrcsCts;
    private CueSheetAnalysis? _findCrcsCueAnalysis;
    private static readonly Regex FindCrcsTargetTrackNumberRegex = new(
        @"(?i)\btrack[\s_\-\(\[]*0*(?<number>\d{1,2})\b",
        RegexOptions.Compiled);
    private CancellationTokenSource? _concatenateCts;
    private CancellationTokenSource? _iso2BinCts;
    private CancellationTokenSource? _skeletonCts;
    private CancellationTokenSource? _irdCts;
    private CancellationTokenSource? _sha1CatalogueCts;
    private CancellationTokenSource? _audioHeadsTailsCts;
    private CancellationTokenSource? _discEvidenceCts;

    // Preserve the last real Normal client size. On some Avalonia/Windows paths
    // the platform can report the maximized dimensions during the restore
    // transition, so normal-size capture is deferred and suppressed while a
    // restore is pending.
    private Size _lastNormalClientSize = new(1060, 780);
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _normalSizeInitialized;
    private bool _normalRestorePending;
    private bool _applyingNormalSize;
    private string _applicationTitle = "DumpToolbox";

    public MainWindow()
    {
        _discEvidenceService = new DiscEvidenceService(_skeletoolCatalogueService);
        InitializeComponent();
        ApplyApplicationTitle();
        ConcatFilesList.ItemsSource = _concatenateFiles;
        SkeletonTree.ItemsSource = _skeletonTreeRoots;
        InitializeAudioRecoveryTab();
        InitializeIsoExtractorTab();
        InitializeDicTab();
        InitializeIrdTab();
        InitializeActivityLogLayout();
        InitializeUtilityTabs();
        InitializeUserSettings();
        InitializeCloseGuard();
        InitializeDiscEvidenceDevTools();
        _ = RefreshSha1CatalogueRootsAsync();
        _ = RefreshAudioHeadsTailsRootsAsync();

        Opened += (_, _) =>
        {
            _previousWindowState = WindowState;
            if (WindowState == WindowState.Normal)
                CaptureNormalWindowSize(ClientSize);
        };

        Resized += MainWindow_Resized;
    }

    private void ApplyApplicationTitle()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        _applicationTitle = version is { Build: >= 0 }
            ? $"DumpToolbox {version.Major}.{version.Minor}.{version.Build}"
            : "DumpToolbox";
        Title = _applicationTitle;
    }

    private void SetWindowStatus(string? status = null)
    {
        Title = string.IsNullOrWhiteSpace(status)
            ? _applicationTitle
            : $"{_applicationTitle} — {status}";
    }

    private void MainWindow_Resized(object? sender, WindowResizedEventArgs e)
    {
        if (_applyingNormalSize || _normalRestorePending)
            return;

        Size reportedSize = e.ClientSize;

        // A maximize/restore notification can arrive before WindowState catches
        // up. Re-check on the next dispatcher turn and only remember the size if
        // the window is still genuinely Normal.
        Dispatcher.UIThread.Post(() =>
        {
            if (_applyingNormalSize || _normalRestorePending ||
                WindowState != WindowState.Normal)
                return;

            CaptureNormalWindowSize(reportedSize);
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
            return;

        WindowState newState = WindowState;
        WindowState oldState = _previousWindowState;
        _previousWindowState = newState;

        if (oldState != WindowState.Normal &&
            newState == WindowState.Normal &&
            _normalSizeInitialized)
        {
            _normalRestorePending = true;

            // Let the native restore finish first. Avalonia's Win32 backend can
            // otherwise update the restore bounds later in the same UI turn.
            Dispatcher.UIThread.Post(RestoreNormalWindowSize);
        }
    }

    private void CaptureNormalWindowSize(Size size)
    {
        if (_applyingNormalSize || _normalRestorePending)
            return;

        if (double.IsFinite(size.Width) && double.IsFinite(size.Height) &&
            size.Width >= MinWidth && size.Height >= MinHeight)
        {
            _lastNormalClientSize = size;
            _normalSizeInitialized = true;
        }
    }

    private void RestoreNormalWindowSize()
    {
        if (WindowState != WindowState.Normal || !_normalSizeInitialized)
        {
            _normalRestorePending = false;
            return;
        }

        _applyingNormalSize = true;
        try
        {
            Width = Math.Max(MinWidth, _lastNormalClientSize.Width);
            Height = Math.Max(MinHeight, _lastNormalClientSize.Height);
        }
        finally
        {
            // Ignore the resize generated by our own Width/Height assignment,
            // then resume tracking subsequent user-selected Normal dimensions.
            Dispatcher.UIThread.Post(() =>
            {
                _applyingNormalSize = false;
                _normalRestorePending = false;
            });
        }
    }

    // FindCRCs tab

    private static void AppendLog(TextBox logBox, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        logBox.Text = string.IsNullOrEmpty(logBox.Text) ? line : logBox.Text + Environment.NewLine + line;
        logBox.CaretIndex = logBox.Text?.Length ?? 0;
        KeepLogAtLeftEdge(logBox);
    }

    private static void KeepLogAtLeftEdge(TextBox logBox)
    {
        // Moving the caret to the end keeps the newest line visible, but Avalonia will also
        // horizontally scroll to the end of a long line. Reset X after layout so every log
        // remains anchored at column zero while continuing to follow new output vertically.
        Dispatcher.UIThread.Post(() =>
        {
            ScrollViewer? viewer = logBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewer is null)
                return;

            double bottom = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            viewer.Offset = new Vector(0, bottom);
        }, DispatcherPriority.Background);
    }

    private Task<EofSlackAmbiguityDecision> ResolveEofSlackAmbiguityAsync(
        EofSlackAmbiguityRequest request,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<EofSlackAmbiguityDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        Dispatcher.UIThread.Post(async () =>
        {
            if (completion.Task.IsCompleted)
                return;

            var choices = new StackPanel { Spacing = 8 };
            choices.Children.Add(new TextBlock
            {
                Text = "Multiple enabled EOF-slack observations match this disc. These alternatives have each been observed on discs with the same visible mastering signature. Choose one, or test every observation against the expected destination hashes.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            foreach (EofSlackRule rule in request.MatchingRules)
            {
                var button = new Button
                {
                    Content = $"Use {rule.DeltaSectors:N0} sectors — {rule.Name}",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left
                };
                string section = rule.Section;
                button.Click += (_, _) =>
                {
                    completion.TrySetResult(new EofSlackAmbiguityDecision(section));
                    if (button.GetVisualRoot() is Window owner)
                        owner.Close();
                };
                choices.Children.Add(button);
            }

            if (request.CanTryAllAndVerify)
            {
                var tryAll = new Button
                {
                    Content = "Try all observations and keep the one matching destination hashes",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                tryAll.Click += (_, _) =>
                {
                    completion.TrySetResult(new EofSlackAmbiguityDecision(TryAllAndVerify: true));
                    if (tryAll.GetVisualRoot() is Window owner)
                        owner.Close();
                };
                choices.Children.Add(tryAll);
            }

            var skip = new Button { Content = "Leave EOF slack zero-filled", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            skip.Click += (_, _) =>
            {
                completion.TrySetResult(new EofSlackAmbiguityDecision());
                if (skip.GetVisualRoot() is Window owner)
                    owner.Close();
            };
            choices.Children.Add(skip);

            var dialog = new Window
            {
                Title = "EOF slack mastering ambiguity",
                Width = 720,
                MinHeight = 300,
                CanResize = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(18),
                        Spacing = 14,
                        Children =
                        {
                            new TextBlock { Text = $"Application: {request.ApplicationId}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new TextBlock { Text = $"System ID: {(string.IsNullOrWhiteSpace(request.SystemId) ? "<blank>" : request.SystemId)}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            choices
                        }
                    }
                }
            };
            ApplyThemeClassToWindow(dialog);
            dialog.Closed += (_, _) => completion.TrySetResult(new EofSlackAmbiguityDecision());
            await dialog.ShowDialog(this);
        });

        return completion.Task;
    }

    private Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Confirm")
    {
        var completion = new TaskCompletionSource<bool>();

        var confirm = new Button
        {
            Content = confirmText,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };

        var dialog = new Window
        {
            Title = title,
            Width = 600,
            MinHeight = 230,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    buttons
                }
            }
        };

        ApplyThemeClassToWindow(dialog);
        confirm.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) => completion.TrySetResult(false);

        _ = dialog.ShowDialog(this);
        return completion.Task;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };

        ApplyThemeClassToWindow(dialog);

        if (dialog.Content is StackPanel panel && panel.Children[1] is Button ok)
            ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }
}
