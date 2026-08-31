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
    private void BuildDicTree(SkeletonInspectionResult inspection)
    {
        _dicTreeRoots.Clear();
        _dicNodes.Clear();

        var rootFolders = new Dictionary<string, SkeletonTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonContentEntry entry in inspection.Entries.Where(e => e.SpecialKind == SkeletonSpecialKind.None))
        {
            string[] parts = entry.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            ObservableCollection<SkeletonTreeNode> children = _dicTreeRoots;
            string accumulated = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                accumulated += "/" + parts[i];
                if (!rootFolders.TryGetValue(accumulated, out SkeletonTreeNode? folder))
                {
                    folder = new SkeletonTreeNode(parts[i]);
                    rootFolders[accumulated] = folder;
                    children.Add(folder);
                }
                children = folder.Children;
            }

            var fileNode = new SkeletonTreeNode(parts[^1], entry);
            SetInitialDicNodeStatus(fileNode, entry);
            children.Add(fileNode);
            _dicNodes[entry.Path] = fileNode;
        }

        SkeletonContentEntry[] special = inspection.Entries.Where(e => e.SpecialKind != SkeletonSpecialKind.None).ToArray();
        if (special.Length > 0)
        {
            var specialRoot = new SkeletonTreeNode("[Special / metadata entries]");
            foreach (SkeletonContentEntry entry in special)
            {
                var node = new SkeletonTreeNode(entry.Path, entry);
                SetInitialDicNodeStatus(node, entry);
                specialRoot.Children.Add(node);
                _dicNodes[entry.Path] = node;
            }
            _dicTreeRoots.Add(specialRoot);
        }
    }

    private static void SetInitialDicNodeStatus(SkeletonTreeNode node, SkeletonContentEntry entry)
    {
        node.SourcePath = null;
        if (!entry.CanRestore)
            node.Status = "!";
        else if (entry.IsEmpty)
            node.Status = "✓0";
        else if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                 string.Equals(entry.Sha1, SkeletonResurrectionService.ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            node.Status = "✓0";
        else if (entry.RequiresSource)
            node.Status = "○";
        else
            node.Status = "?";
    }

    private void ResetDicMatchStatuses()
    {
        if (_dicInspection is null)
            return;

        foreach (SkeletonContentEntry entry in _dicInspection.Entries)
        {
            if (_dicNodes.TryGetValue(entry.Path, out SkeletonTreeNode? node))
                SetInitialDicNodeStatus(node, entry);
        }
    }

    private void MarkDicMissingStatuses()
    {
        if (_dicInspection is null)
            return;

        foreach (SkeletonContentEntry entry in _dicInspection.Entries)
        {
            if (!_dicNodes.TryGetValue(entry.Path, out SkeletonTreeNode? node))
                continue;

            if (_dicAppliedEntries.Contains(entry.Path))
            {
                node.Status = "✓R";
                node.SourcePath = _dicState?.LastOutputPath;
            }
            else if (_dicMatches.TryGetValue(entry.Path, out SkeletonSourceMatch? match))
            {
                node.Status = "✓";
                node.SourcePath = match.SourcePath;
            }
            else if (IsRequiredDicSourceEntry(entry))
            {
                node.Status = "✗";
                node.SourcePath = null;
            }
        }
    }

    private static bool IsRequiredDicSourceEntry(SkeletonContentEntry entry)
    {
        if (!entry.CanRestore || entry.IsEmpty)
            return false;
        if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
            string.Equals(entry.Sha1, SkeletonResurrectionService.ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            return false;
        return entry.RequiresSource;
    }

    private void SetDicRunning(bool running)
    {
        DicCancelButton.IsEnabled = running;
        DicLogBrowseButton.IsEnabled = !running;
        DicSourceBrowseButton.IsEnabled = !running;
        DicDonorBrowseButton.IsEnabled = !running;
        DicOutputBrowseButton.IsEnabled = !running;
        DicLoadButton.IsEnabled = !running;
        DicAllowMissingCheckBox.IsEnabled = !running;
        DicForceRehashCheckBox.IsEnabled = !running;
        DicVerboseLoggingCheckBox.IsEnabled = !running;
        DicLogPathBox.IsReadOnly = running;
        DicSourceFolderBox.IsReadOnly = running;
        DicDonorImageBox.IsReadOnly = running;
        DicOutputBox.IsReadOnly = running;

        if (running)
        {
            DicMatchButton.IsEnabled = false;
            DicDonorScanButton.IsEnabled = false;
            DicResurrectButton.IsEnabled = false;
            DicClearStateButton.IsEnabled = false;
        }
    }

    private void UpdateDicActionButtons()
    {
        bool loaded = _dicInspection is not null && _dicCts is null;
        bool donorRequirementsRequired = _dicInspection?.DonorRequirements?.Any(requirement => requirement.BlocksResurrection) == true;
        DicMatchButton.IsEnabled = loaded;
        DicDonorScanButton.IsEnabled = loaded;
        DicResurrectButton.IsEnabled = loaded && (!donorRequirementsRequired || _dicDonorRequirementsSatisfied);
        DicClearStateButton.IsEnabled = loaded && !string.IsNullOrWhiteSpace(_dicStatePath);
    }

}
