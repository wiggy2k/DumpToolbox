using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DumpToolbox.Core;

namespace DumpToolbox;

public sealed class IrdTreeNode : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private string? _sourcePath;

    public IrdTreeNode(string name, Ps3IrdFileEntry? entry = null)
    {
        Name = name;
        Entry = entry;
    }

    public string Name { get; }
    public Ps3IrdFileEntry? Entry { get; }
    public ObservableCollection<IrdTreeNode> Children { get; } = new();
    public bool IsFolder => Entry is null;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsText));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            if (_sourcePath == value) return;
            _sourcePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsText));
        }
    }

    public string DetailsText
    {
        get
        {
            if (IsFolder) return Name;
            string source = string.IsNullOrWhiteSpace(SourcePath) ? string.Empty : $"  ← {Path.GetFileName(SourcePath)}";
            string extentText = Entry!.IsMultiExtent ? $", {Entry.ExtentCount} extents" : string.Empty;
            return $"{Name}  ({Entry.Length:N0} bytes, LBA {Entry.FirstSector:N0}{extentText}){source}";
        }
    }

    public IBrush? StatusForeground =>
        Status.StartsWith("✓", StringComparison.Ordinal) ? Brushes.LimeGreen :
        Status.StartsWith("✗", StringComparison.Ordinal) ? Brushes.Red :
        Status.StartsWith("!", StringComparison.Ordinal) ? Brushes.Orange :
        null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
