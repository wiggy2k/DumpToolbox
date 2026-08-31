using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private Ps3IrdVerificationResult? _irdVerification;
    private string? _irdVerifiedIrdPath;
    private string? _irdVerifiedSourceFolder;
    private readonly ObservableCollection<IrdTreeNode> _irdTreeRoots = new();
    private readonly Dictionary<string, IrdTreeNode> _irdNodes = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastIrdProgressLogKey;

    private void InitializeIrdTab()
    {
        IrdTree.ItemsSource = _irdTreeRoots;
    }

    private async void IrdBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose PS3 IRD",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PS3 IRD") { Patterns = new[] { "*.ird" } }, FilePickerFileTypes.All }
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        IrdPathBox.Text = path;
        try
        {
            _irdVerification = null;
            _irdVerifiedIrdPath = null;
            _irdVerifiedSourceFolder = null;
            IrdRebuildButton.IsEnabled = false;
            _irdTreeRoots.Clear();
            _irdNodes.Clear();
            Ps3IrdInfo info = _irdService.Inspect(path);
            BuildIrdTree(info);
            IrdInspectionText.Text = $"{info.GameId} — {info.GameName} | IRD v{info.Version} | Game {info.GameVersion} | App {info.AppVersion} | Update {info.UpdateVersion} | {info.Files.Count:N0} files | {info.DiscSize:N0} bytes";
            if (string.IsNullOrWhiteSpace(IrdOutputBox.Text))
                IrdOutputBox.Text = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, _irdService.SuggestOutputPath(path));
            AppendIrdLog($"Loaded IRD: {path}");
            AppendIrdLog(IrdInspectionText.Text);
            AppendIrdLog($"Disc tree populated from IRD filesystem metadata: {info.Files.Count:N0} file(s). LBA and expected size are shown in the tree.");
        }
        catch (Exception ex)
        {
            IrdInspectionText.Text = "IRD load failed.";
            _irdTreeRoots.Clear();
            _irdNodes.Clear();
            AppendIrdLog($"ERROR: {ex.Message}");
        }
    }

    private async void IrdSourceBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose extracted PS3 game folder", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) IrdSourceFolderBox.Text = path;
    }

    private async void IrdKeyBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose PS3 disc key",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PS3 disc key") { Patterns = new[] { "*.key", "*.dkey", "*.txt" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            IrdKeyFileBox.Text = path;
    }

    private async void IrdOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = "rebuilt_ps3.iso";
        try { if (!string.IsNullOrWhiteSpace(IrdPathBox.Text)) suggested = _irdService.SuggestOutputPath(IrdPathBox.Text.Trim()); } catch { }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose rebuilt PS3 ISO",
            SuggestedFileName = suggested,
            FileTypeChoices = new[] { new FilePickerFileType("ISO image") { Patterns = new[] { "*.iso" } } }
        });
        if (file?.TryGetLocalPath() is { } path) IrdOutputBox.Text = path;
    }

    private async void IrdVerifyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_irdCts is not null) return;
        string ird = IrdPathBox.Text?.Trim() ?? "";
        string source = IrdSourceFolderBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(ird) || string.IsNullOrWhiteSpace(source))
        {
            await ShowMessageAsync("DumpToolbox — IRD", "Choose an IRD file and source folder first.");
            return;
        }
        try
        {
            _irdCts = new CancellationTokenSource();
            SetIrdRunning(true);
            ClearIrdLeafStatuses();
            var progress = new Progress<Ps3IrdProgress>(UpdateIrdProgress);
            _irdVerification = await _irdService.VerifySourcesAsync(ird, source, progress, _irdCts.Token);
            _irdVerifiedIrdPath = Path.GetFullPath(ird);
            _irdVerifiedSourceFolder = Path.GetFullPath(source);
            ApplyIrdVerificationToTree(_irdVerification);
            AppendIrdLog($"Verification complete: {_irdVerification.Valid:N0} valid, {_irdVerification.Missing:N0} missing, {_irdVerification.Invalid:N0} invalid.");
            IrdProgressText.Text = _irdVerification.CanRebuild ? "Ready to rebuild" : "Source verification failed";
            IrdRebuildButton.IsEnabled = _irdVerification.CanRebuild;
        }
        catch (OperationCanceledException) { AppendIrdLog("Cancelled."); IrdProgressText.Text = "Cancelled"; }
        catch (Exception ex) { AppendIrdLog($"ERROR: {ex}"); await ShowMessageAsync("DumpToolbox — IRD", ex.Message); }
        finally { _irdCts?.Dispose(); _irdCts = null; SetIrdRunning(false); }
    }

    private async void IrdRebuildButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_irdCts is not null) return;
        string ird = IrdPathBox.Text?.Trim() ?? "";
        string source = IrdSourceFolderBox.Text?.Trim() ?? "";
        string output = IrdOutputBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(ird) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output))
        {
            await ShowMessageAsync("DumpToolbox — IRD", "Choose an IRD file, source folder and output ISO first.");
            return;
        }
        try
        {
            _irdCts = new CancellationTokenSource();
            SetIrdRunning(true);
            var progress = new Progress<Ps3IrdProgress>(UpdateIrdProgress);
            bool encrypt = IrdEncryptCheckBox.IsChecked == true;
            byte[]? discKey = null;
            if (encrypt)
            {
                discKey = _irdService.ResolveDiscKey(IrdKeyFileBox.Text?.Trim(), IrdKeyTextBox.Text?.Trim());
                AppendIrdLog("Encryption enabled: supplied value accepted as a 16-byte PS3 disc key.");
            }

            AppendIrdLog($"Rebuild start: {output}");

            bool canReuseVerification = _irdVerification?.CanRebuild == true
                && string.Equals(_irdVerifiedIrdPath, Path.GetFullPath(ird), StringComparison.OrdinalIgnoreCase)
                && string.Equals(_irdVerifiedSourceFolder, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);

            string rebuildPath = output;
            string? temporaryPlainIso = null;
            if (encrypt)
            {
                temporaryPlainIso = output + ".plain.tmp";
                rebuildPath = temporaryPlainIso;
                try { if (File.Exists(temporaryPlainIso)) File.Delete(temporaryPlainIso); } catch { }
                AppendIrdLog($"Encrypted output requested; temporary plain ISO: {temporaryPlainIso}");
            }

            Ps3IrdRebuildResult result;
            try
            {
                if (canReuseVerification)
                {
                    AppendIrdLog("Reusing successful source verification; files will not be hashed a second time.");
                    result = await _irdService.RebuildVerifiedAsync(ird, source, rebuildPath, _irdVerification!, progress, _irdCts.Token);
                }
                else
                {
                    AppendIrdLog("IRD/source selection changed since verification; verifying sources before rebuild.");
                    result = await _irdService.RebuildAsync(ird, source, rebuildPath, progress, _irdCts.Token);
                }

                if (encrypt)
                {
                    AppendIrdLog("Plain reconstruction complete; encrypting PS3 encrypted regions on a background worker...");
                    result = await Task.Run(
                        () => _irdService.EncryptIsoAsync(ird, rebuildPath, output, discKey!, progress, _irdCts.Token),
                        _irdCts.Token);
                }
            }
            finally
            {
                if (temporaryPlainIso is not null)
                {
                    try
                    {
                        if (File.Exists(temporaryPlainIso)) File.Delete(temporaryPlainIso);
                    }
                    catch (Exception cleanupEx)
                    {
                        AppendIrdLog($"WARNING: Could not remove temporary plain ISO: {cleanupEx.Message}");
                    }
                }
            }

            AppendIrdLog($"Rebuild complete: {result.OutputPath}");
            if (result.RegionVerificationPerformed)
                AppendIrdLog($"IRD region verification: {result.VerifiedRegions}/{result.TotalRegions} matched.");
            else
                AppendIrdLog($"IRD region hashes: {result.TotalRegions} present; encryption was not requested.");
            IrdProgressBar.Value = 100;
            IrdProgressText.Text = encrypt ? "Encrypted + verified" : "Rebuilt";
            await ShowMessageAsync("DumpToolbox — IRD", encrypt
                ? "PS3 ISO rebuilt, encrypted with the supplied disc key, and verified against the IRD region hashes."
                : "PS3 plain ISO rebuilt successfully from the verified IRD sources.");
        }
        catch (OperationCanceledException) { AppendIrdLog("Cancelled."); IrdProgressText.Text = "Cancelled"; }
        catch (Exception ex) { AppendIrdLog($"ERROR: {ex}"); await ShowMessageAsync("DumpToolbox — IRD", ex.Message); }
        finally { _irdCts?.Dispose(); _irdCts = null; SetIrdRunning(false); }
    }

    private void IrdCancelButton_Click(object? sender, RoutedEventArgs e) => _irdCts?.Cancel();

    private void UpdateIrdProgress(Ps3IrdProgress p)
    {
        IrdProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
        IrdProgressText.Text = p.Message;

        // Encryption reports every processing chunk so the progress bar remains live.
        // Do not duplicate the same region message into the activity log thousands of times.
        string logKey = $"{p.Phase}\0{p.Message}";
        if (!string.Equals(_lastIrdProgressLogKey, logKey, StringComparison.Ordinal))
        {
            _lastIrdProgressLogKey = logKey;
            AppendIrdLog($"{p.Phase}: {p.Message}");
        }
    }

    private void AppendIrdLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _irdActivityLogText.Append(line);
        IrdLogBox.Text = _irdActivityLogText.ToString();
        IrdLogBox.CaretIndex = IrdLogBox.Text.Length;
        UpdateIrdDetachedLog();
    }

    private void SetIrdRunning(bool running)
    {
        IrdBrowseButton.IsEnabled = !running;
        IrdSourceBrowseButton.IsEnabled = !running;
        IrdOutputBrowseButton.IsEnabled = !running;
        IrdKeyBrowseButton.IsEnabled = !running;
        IrdKeyFileBox.IsEnabled = !running;
        IrdKeyTextBox.IsEnabled = !running;
        IrdEncryptCheckBox.IsEnabled = !running;
        IrdVerifyButton.IsEnabled = !running;
        IrdRebuildButton.IsEnabled = !running && (_irdVerification?.CanRebuild ?? false);
        IrdCancelButton.IsEnabled = running;
    }

    private void BuildIrdTree(Ps3IrdInfo info)
    {
        _irdTreeRoots.Clear();
        _irdNodes.Clear();
        var folders = new Dictionary<string, IrdTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (Ps3IrdFileEntry entry in info.Files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            string normalized = entry.Path.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            ObservableCollection<IrdTreeNode> children = _irdTreeRoots;
            string accumulated = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                accumulated = string.IsNullOrEmpty(accumulated) ? parts[i] : accumulated + "/" + parts[i];
                if (!folders.TryGetValue(accumulated, out IrdTreeNode? folder))
                {
                    folder = new IrdTreeNode(parts[i]);
                    folders[accumulated] = folder;
                    children.Add(folder);
                }
                children = folder.Children;
            }

            var fileNode = new IrdTreeNode(parts[^1], entry);
            children.Add(fileNode);
            _irdNodes[normalized] = fileNode;
        }
    }

    private void ClearIrdLeafStatuses()
    {
        foreach (IrdTreeNode node in _irdNodes.Values)
        {
            node.Status = string.Empty;
            node.SourcePath = null;
        }
    }

    private void ApplyIrdVerificationToTree(Ps3IrdVerificationResult verification)
    {
        foreach (Ps3IrdFileCheck check in verification.Files)
        {
            string key = check.Entry.Path.Replace('\\', '/').Trim('/');
            if (!_irdNodes.TryGetValue(key, out IrdTreeNode? node)) continue;
            node.Status = check.Status switch
            {
                "OK" => "✓",
                "MISSING" => "✗",
                "INVALID" => "!",
                _ => "?"
            };
            node.SourcePath = check.SourcePath;
        }
    }
}
