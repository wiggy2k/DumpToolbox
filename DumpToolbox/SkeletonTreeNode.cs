using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DumpToolbox.Core;

namespace DumpToolbox;

public sealed class SkeletonTreeNode : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private string? _sourcePath;

    public SkeletonTreeNode(string name, SkeletonContentEntry? entry = null)
    {
        Name = name;
        Entry = entry;
    }

    public string Name { get; }
    public SkeletonContentEntry? Entry { get; }
    public ObservableCollection<SkeletonTreeNode> Children { get; } = new();
    public bool IsFolder => Entry is null;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(DetailsText));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            if (_sourcePath == value)
                return;
            _sourcePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(DetailsText));
        }
    }

    public string DisplayText
    {
        get
        {
            if (IsFolder)
                return Name;

            string size = $"  ({FormatBytes(Entry!.DataLength)})";
            string source = string.IsNullOrWhiteSpace(SourcePath) ? string.Empty : $"  ← {Path.GetFileName(SourcePath)}";
            string status = string.IsNullOrWhiteSpace(Status) ? "○" : Status;
            return $"{status} {Name}{size}{source}";
        }
    }


    public string DetailsText
    {
        get
        {
            if (IsFolder)
                return Name;

            string size = $"  ({FormatBytes(Entry!.DataLength)})";
            string source = string.IsNullOrWhiteSpace(SourcePath) ? string.Empty : $"  ← {Path.GetFileName(SourcePath)}";
            return $"{Name}{size}{source}";
        }
    }

    public IBrush? StatusForeground =>
        Status.StartsWith("✓", StringComparison.Ordinal) ? Brushes.LimeGreen :
        string.Equals(Status, "✗", StringComparison.Ordinal) ? Brushes.Red :
        null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatBytes(long bytes)
    {
        return $"{bytes:N0} bytes";
    }
}
