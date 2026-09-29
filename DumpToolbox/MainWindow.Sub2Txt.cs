using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private const int Sub2TxtPageSize = 500;
    private readonly SubchannelTextService _subchannelTextService = new();
    private CancellationTokenSource? _sub2TxtCts;
    private string? _sub2TxtRenderedTempPath;
    private string? _sub2TxtRenderedSourcePath;
    private SubchannelTextFileResult? _sub2TxtRenderedResult;
    private long _sub2TxtPageIndex;

    private async void Sub2TxtBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a subchannel file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CD subchannel files") { Patterns = ["*.sub", "*.subcode", "*.subchannel"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Sub2TxtInputBox.Text = path;
            ClearSub2TxtOutput();
        }
    }

    private async void Sub2TxtViewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_sub2TxtCts is not null)
            return;

        try
        {
            string inputPath = Sub2TxtInputBox.Text?.Trim() ?? string.Empty;
            DeleteSub2TxtTemporaryOutput();
            _sub2TxtRenderedSourcePath = null;
            _sub2TxtRenderedResult = null;
            _sub2TxtPageIndex = 0;
            Sub2TxtOutputBox.Text = string.Empty;
            Sub2TxtPageText.Text = string.Empty;
            Sub2TxtMcnBox.Text = string.Empty;
            Sub2TxtIsrcBox.Text = string.Empty;
            Sub2TxtProgressBar.Value = 0;

            _sub2TxtCts = new CancellationTokenSource();
            SetSub2TxtRunning(true);
            Sub2TxtStatusText.Text = "Reading subchannel data...";
            SetWindowStatus("Sub2TXT — starting");

            var progress = new Progress<SubchannelTextProgress>(p =>
            {
                Sub2TxtProgressBar.Value = p.Fraction * 100;
                Sub2TxtStatusText.Text = $"{p.SectorsProcessed:N0}/{p.TotalSectors:N0} sectors";
                SetWindowStatus($"Sub2TXT — {p.Fraction:P0}");
            });

            string temporaryPath = Path.Combine(
                Path.GetTempPath(),
                $"DumpToolbox-Sub2TXT-{Guid.NewGuid():N}.txt");
            SubchannelTextFileResult result = await _subchannelTextService.RenderToFileAsync(
                inputPath,
                temporaryPath,
                progress,
                _sub2TxtCts.Token);

            _sub2TxtRenderedTempPath = temporaryPath;
            _sub2TxtRenderedSourcePath = Path.GetFullPath(inputPath);
            _sub2TxtRenderedResult = result;
            UpdateSub2TxtMetadataBoxes(result);
            await LoadSub2TxtPageAsync(0, _sub2TxtCts.Token);
            Sub2TxtProgressBar.Value = 100;
            UpdateSub2TxtCompletedStatus();
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            Sub2TxtStatusText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            Sub2TxtStatusText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Sub2TXT", ex.Message);
        }
        finally
        {
            _sub2TxtCts?.Dispose();
            _sub2TxtCts = null;
            SetSub2TxtRunning(false);
        }
    }

    private async void Sub2TxtSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_sub2TxtCts is not null || _sub2TxtRenderedTempPath is null)
            return;

        string suggested = "subReadable.txt";
        if (!string.IsNullOrWhiteSpace(_sub2TxtRenderedSourcePath))
            suggested = Path.GetFileName(SubchannelTextService.SuggestOutputPath(_sub2TxtRenderedSourcePath));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save readable subchannel data",
            SuggestedFileName = suggested,
            FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }]
        });

        if (file?.TryGetLocalPath() is not { } outputPath)
            return;

        try
        {
            string fullOutputPath = Path.GetFullPath(outputPath);
            if (_sub2TxtRenderedSourcePath is not null &&
                string.Equals(fullOutputPath, _sub2TxtRenderedSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The text output cannot overwrite the source subchannel file.");
            }

            _sub2TxtCts = new CancellationTokenSource();
            SetSub2TxtRunning(true);
            Sub2TxtStatusText.Text = "Saving...";
            SetWindowStatus("Sub2TXT — saving");

            string partialPath = fullOutputPath + ".partial";
            try
            {
                await using (var source = new FileStream(
                    _sub2TxtRenderedTempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                    partialPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(destination, 1024 * 1024, _sub2TxtCts.Token);
                    await destination.FlushAsync(_sub2TxtCts.Token);
                }
                File.Move(partialPath, fullOutputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }

            Sub2TxtStatusText.Text = $"Saved {Path.GetFileName(fullOutputPath)}";
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            Sub2TxtStatusText.Text = "Save cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            Sub2TxtStatusText.Text = "Save error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Sub2TXT", ex.Message);
        }
        finally
        {
            _sub2TxtCts?.Dispose();
            _sub2TxtCts = null;
            SetSub2TxtRunning(false);
        }
    }

    private async void Sub2TxtPreviousPageButton_Click(object? sender, RoutedEventArgs e)
        => await NavigateSub2TxtPageAsync(_sub2TxtPageIndex - 1);

    private async void Sub2TxtNextPageButton_Click(object? sender, RoutedEventArgs e)
        => await NavigateSub2TxtPageAsync(_sub2TxtPageIndex + 1);

    private async void Sub2TxtJumpToLbaZeroButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_sub2TxtCts is not null ||
            _sub2TxtRenderedResult is not { } result ||
            _sub2TxtRenderedTempPath is null)
            return;

        try
        {
            _sub2TxtCts = new CancellationTokenSource();
            SetSub2TxtRunning(true);
            Sub2TxtStatusText.Text = "Locating LBA 0...";

            long? lineIndex = result.Format == SubchannelFileFormat.RedumperMultiplexed
                ? -SubchannelTextService.RedumperFirstLba
                : await Task.Run(
                    () => FindSub2TxtLbaZeroLine(_sub2TxtRenderedTempPath, _sub2TxtCts.Token),
                    _sub2TxtCts.Token);

            if (lineIndex is null || lineIndex < 0 || lineIndex >= result.SectorCount)
            {
                Sub2TxtStatusText.Text = "LBA 0 was not found";
                await ShowMessageAsync(
                    "DumpToolbox — Sub2TXT",
                    "This subchannel file does not contain a readable LBA 0 entry.");
                return;
            }

            await LoadSub2TxtPageAsync(
                lineIndex.Value / Sub2TxtPageSize,
                _sub2TxtCts.Token,
                lineIndex.Value);
            UpdateSub2TxtCompletedStatus();
        }
        catch (OperationCanceledException)
        {
            UpdateSub2TxtCompletedStatus();
        }
        catch (Exception ex)
        {
            Sub2TxtStatusText.Text = "LBA jump error";
            await ShowMessageAsync("DumpToolbox — Sub2TXT", ex.Message);
        }
        finally
        {
            _sub2TxtCts?.Dispose();
            _sub2TxtCts = null;
            SetSub2TxtRunning(false);
        }
    }

    private async Task NavigateSub2TxtPageAsync(long pageIndex)
    {
        if (_sub2TxtCts is not null || _sub2TxtRenderedResult is null)
            return;

        try
        {
            _sub2TxtCts = new CancellationTokenSource();
            SetSub2TxtRunning(true);
            Sub2TxtStatusText.Text = "Loading page...";
            await LoadSub2TxtPageAsync(pageIndex, _sub2TxtCts.Token);
            UpdateSub2TxtCompletedStatus();
        }
        catch (OperationCanceledException)
        {
            UpdateSub2TxtCompletedStatus();
        }
        catch (Exception ex)
        {
            Sub2TxtStatusText.Text = "Page error";
            await ShowMessageAsync("DumpToolbox — Sub2TXT", ex.Message);
        }
        finally
        {
            _sub2TxtCts?.Dispose();
            _sub2TxtCts = null;
            SetSub2TxtRunning(false);
        }
    }

    private async Task LoadSub2TxtPageAsync(
        long pageIndex,
        CancellationToken cancellationToken,
        long? focusLine = null)
    {
        if (_sub2TxtRenderedTempPath is null || _sub2TxtRenderedResult is null)
            return;

        long pageCount = GetSub2TxtPageCount();
        long selectedPage = Math.Clamp(pageIndex, 0, Math.Max(0, pageCount - 1));
        long firstSector = selectedPage * Sub2TxtPageSize;
        string temporaryPath = _sub2TxtRenderedTempPath;

        string pageText = await Task.Run(
            () => ReadSub2TxtPage(temporaryPath, firstSector, Sub2TxtPageSize, cancellationToken),
            cancellationToken);

        _sub2TxtPageIndex = selectedPage;
        Sub2TxtOutputBox.Text = pageText;
        int lineWithinPage = focusLine is { } requestedLine
            ? checked((int)(requestedLine - firstSector))
            : 0;
        Sub2TxtOutputBox.CaretIndex = FindSub2TxtLineStart(pageText, lineWithinPage);
        Sub2TxtOutputBox.ScrollToLine(lineWithinPage);

        long lastSector = Math.Min(firstSector + Sub2TxtPageSize, _sub2TxtRenderedResult.SectorCount);
        Sub2TxtPageText.Text =
            $"Sectors {firstSector + 1:N0}–{lastSector:N0} of {_sub2TxtRenderedResult.SectorCount:N0} • Page {selectedPage + 1:N0}/{pageCount:N0}";
    }

    private static string ReadSub2TxtPage(
        string path,
        long firstLine,
        int lineCount,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(input);

        for (long line = 0; line < firstLine; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ReadLine() is null)
                return string.Empty;
        }

        var output = new System.Text.StringBuilder(lineCount * 192);
        for (int line = 0; line < lineCount; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? text = reader.ReadLine();
            if (text is null)
                break;
            output.Append(text).Append("\r\n");
        }
        return output.ToString();
    }

    private static long? FindSub2TxtLbaZeroLine(string path, CancellationToken cancellationToken)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(input);

        for (long lineIndex = 0; ; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = reader.ReadLine();
            if (line is null)
                return null;
            if (line.StartsWith("LBA[000000, 0000000]:", StringComparison.Ordinal))
                return lineIndex;
        }
    }

    private static int FindSub2TxtLineStart(string text, int lineIndex)
    {
        int position = 0;
        for (int line = 0; line < lineIndex; line++)
        {
            int newline = text.IndexOf('\n', position);
            if (newline < 0)
                return 0;
            position = newline + 1;
        }
        return position;
    }

    private void Sub2TxtCancelButton_Click(object? sender, RoutedEventArgs e) => _sub2TxtCts?.Cancel();

    private void Sub2TxtClearButton_Click(object? sender, RoutedEventArgs e)
    {
        Sub2TxtInputBox.Text = string.Empty;
        ClearSub2TxtOutput();
    }

    private void ClearSub2TxtOutput()
    {
        DeleteSub2TxtTemporaryOutput();
        _sub2TxtRenderedSourcePath = null;
        _sub2TxtRenderedResult = null;
        _sub2TxtPageIndex = 0;
        Sub2TxtOutputBox.Text = string.Empty;
        Sub2TxtPageText.Text = string.Empty;
        Sub2TxtMcnBox.Text = string.Empty;
        Sub2TxtIsrcBox.Text = string.Empty;
        Sub2TxtProgressBar.Value = 0;
        Sub2TxtStatusText.Text = "Ready";
        Sub2TxtSaveButton.IsEnabled = false;
        Sub2TxtJumpToLbaZeroButton.IsEnabled = false;
        Sub2TxtPreviousPageButton.IsEnabled = false;
        Sub2TxtNextPageButton.IsEnabled = false;
    }

    private void DeleteSub2TxtTemporaryOutput()
    {
        string? temporaryPath = _sub2TxtRenderedTempPath;
        _sub2TxtRenderedTempPath = null;
        if (temporaryPath is not null && File.Exists(temporaryPath))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // The operating system will clean its temporary directory later.
            }
        }
    }

    private long GetSub2TxtPageCount()
        => _sub2TxtRenderedResult is null
            ? 0
            : (_sub2TxtRenderedResult.SectorCount + Sub2TxtPageSize - 1) / Sub2TxtPageSize;

    private void UpdateSub2TxtCompletedStatus()
    {
        if (_sub2TxtRenderedResult is not { } result)
            return;

        string formatName = SubchannelTextService.GetFormatDisplayName(result.Format);
        Sub2TxtStatusText.Text =
            $"{formatName} • {result.SectorCount:N0} sectors • {result.OutputLength / 1048576.0:N1} MiB text";
    }

    private void UpdateSub2TxtMetadataBoxes(SubchannelTextFileResult result)
    {
        Sub2TxtMcnBox.Text = result.Mcn ?? string.Empty;
        Sub2TxtIsrcBox.Text = string.Join(
            "; ",
            result.IsrcByTrack.Select(pair => $"Track {pair.Key:D2}: {pair.Value}"));
    }

    private void SetSub2TxtRunning(bool running)
    {
        Sub2TxtInputBox.IsReadOnly = running;
        Sub2TxtBrowseButton.IsEnabled = !running;
        Sub2TxtViewButton.IsEnabled = !running;
        Sub2TxtCancelButton.IsEnabled = running;
        Sub2TxtSaveButton.IsEnabled = !running && _sub2TxtRenderedTempPath is not null;
        Sub2TxtClearButton.IsEnabled = !running;
        Sub2TxtClearSavedInputsButton.IsEnabled = !running;
        long pageCount = GetSub2TxtPageCount();
        Sub2TxtJumpToLbaZeroButton.IsEnabled = !running && _sub2TxtRenderedResult is not null;
        Sub2TxtPreviousPageButton.IsEnabled = !running && _sub2TxtPageIndex > 0;
        Sub2TxtNextPageButton.IsEnabled = !running && _sub2TxtPageIndex + 1 < pageCount;
    }
}
