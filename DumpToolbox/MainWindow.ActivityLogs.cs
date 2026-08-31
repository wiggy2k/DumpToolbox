using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly StringBuilder _skeletonActivityLogText = new();
    private Window? _audioActivityLogWindow;
    private TextBox? _audioActivityLogWindowBox;
    private bool _audioLogUndocked;
    private Window? _skeletonActivityLogWindow;
    private TextBox? _skeletonActivityLogWindowBox;
    private Window? _dicActivityLogWindow;
    private TextBox? _dicActivityLogWindowBox;
    private readonly StringBuilder _irdActivityLogText = new();
    private Window? _irdActivityLogWindow;
    private TextBox? _irdActivityLogWindowBox;
    private bool _skeletonLogUndocked;
    private bool _dicLogUndocked;
    private bool _irdLogUndocked;
    private GridLength _skeletonTreeColumnWidth = new(1, GridUnitType.Star);
    private GridLength _skeletonLogColumnWidth = new(1, GridUnitType.Star);
    private GridLength _dicTreeColumnWidth = new(1, GridUnitType.Star);
    private GridLength _dicLogColumnWidth = new(1, GridUnitType.Star);
    private GridLength _irdTreeColumnWidth = new(1, GridUnitType.Star);
    private GridLength _irdLogColumnWidth = new(1, GridUnitType.Star);

    private void InitializeActivityLogLayout()
    {
        UpdateSkeletonActivityLogButtons();
        UpdateAudioActivityLogButtons();
        UpdateDicActivityLogButtons();
        UpdateIrdActivityLogButtons();
    }

    private void AudioActivityLogButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLogUndocked)
        {
            ShowAudioActivityLogWindow();
            return;
        }

        AudioLogPane.IsVisible = !AudioLogPane.IsVisible;
        UpdateAudioActivityLogButtons();
    }

    private void AudioLogDockButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLogUndocked)
            DockAudioActivityLog();
        else
            UndockAudioActivityLog();
    }

    private void SkeletonActivityLogButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_skeletonLogUndocked)
        {
            ShowSkeletonActivityLogWindow();
            return;
        }

        SetSkeletonDockedLogVisible(!SkeletonLogPane.IsVisible);
    }

    private void SkeletonLogDockButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_skeletonLogUndocked)
            DockSkeletonActivityLog();
        else
            UndockSkeletonActivityLog();
    }

    private void DicActivityLogButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_dicLogUndocked)
        {
            ShowDicActivityLogWindow();
            return;
        }

        SetDicDockedLogVisible(!DicLogPane.IsVisible);
    }

    private void DicLogDockButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_dicLogUndocked)
            DockDicActivityLog();
        else
            UndockDicActivityLog();
    }

    private void IrdActivityLogButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_irdLogUndocked)
        {
            ShowIrdActivityLogWindow();
            return;
        }

        SetIrdDockedLogVisible(!IrdLogPane.IsVisible);
    }

    private void IrdLogDockButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_irdLogUndocked)
            DockIrdActivityLog();
        else
            UndockIrdActivityLog();
    }

    private void SetSkeletonDockedLogVisible(bool visible)
    {
        if (!visible && SkeletonLogPane.IsVisible)
        {
            _skeletonTreeColumnWidth = SkeletonWorkspaceGrid.ColumnDefinitions[0].Width;
            _skeletonLogColumnWidth = SkeletonWorkspaceGrid.ColumnDefinitions[2].Width;
        }

        SkeletonLogPane.IsVisible = visible;
        SkeletonLogSplitter.IsVisible = visible;
        SkeletonWorkspaceGrid.ColumnDefinitions[0].Width = visible ? NonZero(_skeletonTreeColumnWidth) : new GridLength(1, GridUnitType.Star);
        SkeletonWorkspaceGrid.ColumnDefinitions[1].Width = visible ? new GridLength(6) : new GridLength(0);
        SkeletonWorkspaceGrid.ColumnDefinitions[2].Width = visible ? NonZero(_skeletonLogColumnWidth) : new GridLength(0);
        UpdateSkeletonActivityLogButtons();
    }

    private void SetDicDockedLogVisible(bool visible)
    {
        if (!visible && DicLogPane.IsVisible)
        {
            _dicTreeColumnWidth = DicWorkspaceGrid.ColumnDefinitions[0].Width;
            _dicLogColumnWidth = DicWorkspaceGrid.ColumnDefinitions[2].Width;
        }

        DicLogPane.IsVisible = visible;
        DicLogSplitter.IsVisible = visible;
        DicWorkspaceGrid.ColumnDefinitions[0].Width = visible ? NonZero(_dicTreeColumnWidth) : new GridLength(1, GridUnitType.Star);
        DicWorkspaceGrid.ColumnDefinitions[1].Width = visible ? new GridLength(6) : new GridLength(0);
        DicWorkspaceGrid.ColumnDefinitions[2].Width = visible ? NonZero(_dicLogColumnWidth) : new GridLength(0);
        UpdateDicActivityLogButtons();
    }

    private void SetIrdDockedLogVisible(bool visible)
    {
        if (!visible && IrdLogPane.IsVisible)
        {
            _irdTreeColumnWidth = IrdWorkspaceGrid.ColumnDefinitions[0].Width;
            _irdLogColumnWidth = IrdWorkspaceGrid.ColumnDefinitions[2].Width;
        }

        IrdLogPane.IsVisible = visible;
        IrdLogSplitter.IsVisible = visible;
        IrdWorkspaceGrid.ColumnDefinitions[0].Width = visible ? NonZero(_irdTreeColumnWidth) : new GridLength(1, GridUnitType.Star);
        IrdWorkspaceGrid.ColumnDefinitions[1].Width = visible ? new GridLength(6) : new GridLength(0);
        IrdWorkspaceGrid.ColumnDefinitions[2].Width = visible ? NonZero(_irdLogColumnWidth) : new GridLength(0);
        UpdateIrdActivityLogButtons();
    }

    private static GridLength NonZero(GridLength width) =>
        width.Value > 0 ? width : new GridLength(1, GridUnitType.Star);

    private void UndockAudioActivityLog()
    {
        _audioLogUndocked = true;
        AudioLogPane.IsVisible = false;
        ShowAudioActivityLogWindow();
        UpdateAudioActivityLogButtons();
    }

    private void DockAudioActivityLog()
    {
        _audioLogUndocked = false;
        if (_audioActivityLogWindow is not null)
            _audioActivityLogWindow.Close();
        AudioLogPane.IsVisible = true;
        UpdateAudioActivityLogButtons();
    }

    private void UndockSkeletonActivityLog()
    {
        _skeletonLogUndocked = true;
        SetSkeletonDockedLogVisible(false);
        ShowSkeletonActivityLogWindow();
        UpdateSkeletonActivityLogButtons();
    }

    private void DockSkeletonActivityLog()
    {
        _skeletonLogUndocked = false;
        if (_skeletonActivityLogWindow is not null)
            _skeletonActivityLogWindow.Close();
        SetSkeletonDockedLogVisible(true);
        UpdateSkeletonActivityLogButtons();
    }

    private void UndockDicActivityLog()
    {
        _dicLogUndocked = true;
        SetDicDockedLogVisible(false);
        ShowDicActivityLogWindow();
        UpdateDicActivityLogButtons();
    }

    private void DockDicActivityLog()
    {
        _dicLogUndocked = false;
        if (_dicActivityLogWindow is not null)
            _dicActivityLogWindow.Close();
        SetDicDockedLogVisible(true);
        UpdateDicActivityLogButtons();
    }

    private void UndockIrdActivityLog()
    {
        _irdLogUndocked = true;
        SetIrdDockedLogVisible(false);
        ShowIrdActivityLogWindow();
        UpdateIrdActivityLogButtons();
    }

    private void DockIrdActivityLog()
    {
        _irdLogUndocked = false;
        if (_irdActivityLogWindow is not null)
            _irdActivityLogWindow.Close();
        SetIrdDockedLogVisible(true);
        UpdateIrdActivityLogButtons();
    }

    private void ShowAudioActivityLogWindow()
    {
        if (_audioActivityLogWindow is not null)
        {
            _audioActivityLogWindow.Activate();
            return;
        }

        (_audioActivityLogWindow, _audioActivityLogWindowBox) = CreateActivityLogWindow(
            "DumpToolbox — Audio Recovery Activity Log",
            AudioLogBox.Text ?? string.Empty,
            DockAudioActivityLog,
            () =>
            {
                _audioActivityLogWindow = null;
                _audioActivityLogWindowBox = null;
                UpdateAudioActivityLogButtons();
            });
        _audioActivityLogWindow.Show(this);
    }

    private void ShowSkeletonActivityLogWindow()
    {
        if (_skeletonActivityLogWindow is not null)
        {
            _skeletonActivityLogWindow.Activate();
            return;
        }

        (_skeletonActivityLogWindow, _skeletonActivityLogWindowBox) = CreateActivityLogWindow(
            "DumpToolbox — SkeleTool Activity Log",
            _skeletonActivityLogText.ToString(),
            DockSkeletonActivityLog,
            () =>
            {
                _skeletonActivityLogWindow = null;
                _skeletonActivityLogWindowBox = null;
                UpdateSkeletonActivityLogButtons();
            });
        _skeletonActivityLogWindow.Show(this);
    }

    private void ShowDicActivityLogWindow()
    {
        if (_dicActivityLogWindow is not null)
        {
            _dicActivityLogWindow.Activate();
            return;
        }

        (_dicActivityLogWindow, _dicActivityLogWindowBox) = CreateActivityLogWindow(
            "DumpToolbox — DIC Activity Log",
            _dicLogText.ToString(),
            DockDicActivityLog,
            () =>
            {
                _dicActivityLogWindow = null;
                _dicActivityLogWindowBox = null;
                UpdateDicActivityLogButtons();
            });
        _dicActivityLogWindow.Show(this);
    }

    private void ShowIrdActivityLogWindow()
    {
        if (_irdActivityLogWindow is not null)
        {
            _irdActivityLogWindow.Activate();
            return;
        }

        (_irdActivityLogWindow, _irdActivityLogWindowBox) = CreateActivityLogWindow(
            "DumpToolbox — IRD Activity Log",
            _irdActivityLogText.ToString(),
            DockIrdActivityLog,
            () =>
            {
                _irdActivityLogWindow = null;
                _irdActivityLogWindowBox = null;
                UpdateIrdActivityLogButtons();
            });
        _irdActivityLogWindow.Show(this);
    }

    private (Window Window, TextBox LogBox) CreateActivityLogWindow(
        string title,
        string text,
        Action dockAction,
        Action closedAction)
    {
        var logBox = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(logBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(logBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var dockButton = new Button
        {
            Content = "Dock to main window",
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var root = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8
        };
        root.Children.Add(dockButton);
        Grid.SetRow(logBox, 1);
        root.Children.Add(logBox);

        var window = new Window
        {
            Title = title,
            Width = 880,
            Height = 600,
            MinWidth = 520,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        ApplyThemeClassToWindow(window);
        dockButton.Click += (_, _) => dockAction();
        window.Closed += (_, _) => closedAction();
        ScrollDetachedLogToEnd(logBox);
        return (window, logBox);
    }

    private static void ScrollDetachedLogToEnd(TextBox? logBox)
    {
        if (logBox is null)
            return;
        logBox.CaretIndex = logBox.Text?.Length ?? 0;
    }

    private void UpdateAudioDetachedLog()
    {
        if (_audioActivityLogWindowBox is null)
            return;
        _audioActivityLogWindowBox.Text = AudioLogBox.Text ?? string.Empty;
        ScrollDetachedLogToEnd(_audioActivityLogWindowBox);
    }

    private void UpdateSkeletonDetachedLog()
    {
        if (_skeletonActivityLogWindowBox is null)
            return;
        _skeletonActivityLogWindowBox.Text = _skeletonActivityLogText.ToString();
        ScrollDetachedLogToEnd(_skeletonActivityLogWindowBox);
    }

    private void UpdateDicDetachedLog()
    {
        if (_dicActivityLogWindowBox is null)
            return;
        _dicActivityLogWindowBox.Text = _dicLogText.ToString();
        ScrollDetachedLogToEnd(_dicActivityLogWindowBox);
    }

    private void UpdateIrdDetachedLog()
    {
        if (_irdActivityLogWindowBox is null)
            return;
        _irdActivityLogWindowBox.Text = _irdActivityLogText.ToString();
        ScrollDetachedLogToEnd(_irdActivityLogWindowBox);
    }

    private void UpdateAudioActivityLogButtons()
    {
        if (_audioLogUndocked)
        {
            AudioActivityLogButtonText.Text = _audioActivityLogWindow is null ? "Open log" : "Show log";
            AudioLogDockButtonText.Text = "Dock";
        }
        else
        {
            AudioActivityLogButtonText.Text = AudioLogPane.IsVisible ? "Hide" : "Show log";
            AudioLogDockButtonText.Text = "Undock";
        }
    }

    private void UpdateSkeletonActivityLogButtons()
    {
        if (_skeletonLogUndocked)
        {
            SkeletonActivityLogButtonText.Text = _skeletonActivityLogWindow is null ? "Open log" : "Show log";
            SkeletonLogDockButtonText.Text = "Dock";
        }
        else
        {
            SkeletonActivityLogButtonText.Text = SkeletonLogPane.IsVisible ? "Hide" : "Show log";
            SkeletonLogDockButtonText.Text = "Undock";
        }
    }

    private void UpdateDicActivityLogButtons()
    {
        if (_dicLogUndocked)
        {
            DicActivityLogButtonText.Text = _dicActivityLogWindow is null ? "Open log" : "Show log";
            DicLogDockButtonText.Text = "Dock";
        }
        else
        {
            DicActivityLogButtonText.Text = DicLogPane.IsVisible ? "Hide" : "Show log";
            DicLogDockButtonText.Text = "Undock";
        }
    }

    private void UpdateIrdActivityLogButtons()
    {
        if (_irdLogUndocked)
        {
            IrdActivityLogButtonText.Text = _irdActivityLogWindow is null ? "Open log" : "Show log";
            IrdLogDockButtonText.Text = "Dock";
        }
        else
        {
            IrdActivityLogButtonText.Text = IrdLogPane.IsVisible ? "Hide" : "Show log";
            IrdLogDockButtonText.Text = "Undock";
        }
    }
}
