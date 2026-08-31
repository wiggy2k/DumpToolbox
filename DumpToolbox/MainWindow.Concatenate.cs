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
    private async void ConcatAddButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files to concatenate",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
                _concatenateFiles.Add(path);
        }

        if (_concatenateFiles.Count > 0 && string.IsNullOrWhiteSpace(ConcatDestinationBox.Text))
        {
            string first = _concatenateFiles[0];
            string directory = Path.GetDirectoryName(first) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(first);
            string extension = Path.GetExtension(first);
            ConcatDestinationBox.Text = Path.Combine(directory, $"{stem}_concatenated{extension}");
        }
    }

    private void ConcatRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = ConcatFilesList.SelectedIndex;
        if (index < 0 || index >= _concatenateFiles.Count)
            return;

        _concatenateFiles.RemoveAt(index);
        if (_concatenateFiles.Count > 0)
            ConcatFilesList.SelectedIndex = Math.Min(index, _concatenateFiles.Count - 1);
    }

    private void ConcatUpButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = ConcatFilesList.SelectedIndex;
        if (index <= 0 || index >= _concatenateFiles.Count)
            return;

        _concatenateFiles.Move(index, index - 1);
        ConcatFilesList.SelectedIndex = index - 1;
    }

    private void ConcatDownButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = ConcatFilesList.SelectedIndex;
        if (index < 0 || index >= _concatenateFiles.Count - 1)
            return;

        _concatenateFiles.Move(index, index + 1);
        ConcatFilesList.SelectedIndex = index + 1;
    }

    private void ConcatClearButton_Click(object? sender, RoutedEventArgs e) => _concatenateFiles.Clear();

    private async void ConcatDestinationBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggestedName = "concatenated.bin";
        if (_concatenateFiles.Count > 0)
        {
            string first = _concatenateFiles[0];
            suggestedName = $"{Path.GetFileNameWithoutExtension(first)}_concatenated{Path.GetExtension(first)}";
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose destination file",
            SuggestedFileName = suggestedName
        });

        if (file?.TryGetLocalPath() is { } path)
            ConcatDestinationBox.Text = path;
    }

    private async void ConcatStartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_concatenateCts is not null)
            return;

        try
        {
            string[] sources = _concatenateFiles.ToArray();
            string destination = ConcatDestinationBox.Text?.Trim() ?? string.Empty;

            if (sources.Length == 0)
                throw new InvalidOperationException("Add at least one source file.");
            if (string.IsNullOrWhiteSpace(destination))
                throw new InvalidOperationException("Choose a destination filename.");

            bool paddingEnabled = ConcatPaddingCheckBox.IsChecked == true;
            long paddingBytes = 0;
            if (paddingEnabled)
            {
                string paddingText = ConcatPaddingBytesBox.Text?.Trim() ?? string.Empty;
                if (!long.TryParse(paddingText, out paddingBytes) || paddingBytes < 0)
                    throw new InvalidOperationException("Padding bytes must be a whole number of zero or greater.");

                if (paddingBytes == 0)
                    paddingEnabled = false;
            }

            bool checkBoundaries = ConcatBoundaryCheckBox.IsChecked != false;
            var options = new ConcatenateOptions(
                PaddingBytes: paddingEnabled ? paddingBytes : 0,
                CheckPaddingBoundaries: checkBoundaries);

            ConcatLogBox.Text = string.Empty;
            AppendConcatLog($"Destination: {destination}");
            AppendConcatLog($"Source files: {sources.Length:N0}");
            if (options.PaddingEnabled)
            {
                AppendConcatLog(
                    checkBoundaries
                        ? $"Zero padding: {options.PaddingBytes:N0} bytes; boundary safety check enabled."
                        : $"Zero padding: {options.PaddingBytes:N0} bytes between every file; boundary safety check disabled.");
            }
            else
            {
                AppendConcatLog("Zero padding: disabled.");
            }

            _concatenateCts = new CancellationTokenSource();
            SetConcatenateRunning(true);

            var stopwatch = Stopwatch.StartNew();
            int lastLoggedFileIndex = -1;
            var progress = new Progress<ConcatenateProgress>(p =>
            {
                ConcatProgressBar.Value = p.Fraction * 100;
                double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double mibPerSecond = p.BytesWritten / 1048576.0 / seconds;
                ConcatProgressText.Text = $"{p.Fraction:P1}  {mibPerSecond:N1} MiB/s";
                SetWindowStatus($"Concatenate — {p.CurrentFileIndex + 1}/{p.FileCount}");

                if (p.CurrentFileIndex != lastLoggedFileIndex)
                {
                    AppendConcatLog($"Appending {p.CurrentFileIndex + 1}/{p.FileCount}: {p.CurrentFilePath} ({p.CurrentFileLength / 1048576.0:N1} MiB)");
                    lastLoggedFileIndex = p.CurrentFileIndex;
                }
            });

            var activity = new Progress<string>(AppendConcatLog);

            ConcatenateResult result = await _concatenateService.ConcatenateAsync(
                sources, destination, options, progress, activity, _concatenateCts.Token);

            stopwatch.Stop();
            ConcatProgressBar.Value = 100;
            ConcatProgressText.Text = $"Complete — {result.BytesWritten / 1048576.0:N1} MiB";
            AppendConcatLog($"Complete in {stopwatch.Elapsed}. Wrote {result.BytesWritten:N0} bytes.");
            if (options.PaddingEnabled)
            {
                AppendConcatLog(
                    $"Padding written: {result.PaddingBytesWritten:N0} bytes across {result.PaddingBoundariesApplied:N0} boundaries; " +
                    $"skipped {result.PaddingBoundariesSkipped:N0} unsafe boundaries.");
            }
            AppendConcatLog($"Created: {result.DestinationPath}");
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendConcatLog("Concatenation cancelled. Partial output removed.");
            ConcatProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendConcatLog($"ERROR: {ex.Message}");
            ConcatProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Concatenate", ex.Message);
        }
        finally
        {
            _concatenateCts?.Dispose();
            _concatenateCts = null;
            SetConcatenateRunning(false);
        }
    }

    private void ConcatCancelButton_Click(object? sender, RoutedEventArgs e) => _concatenateCts?.Cancel();

    private void SetConcatenateRunning(bool running)
    {
        ConcatStartButton.IsEnabled = !running;
        ConcatCancelButton.IsEnabled = running;
        ConcatAddButton.IsEnabled = !running;
        ConcatRemoveButton.IsEnabled = !running;
        ConcatUpButton.IsEnabled = !running;
        ConcatDownButton.IsEnabled = !running;
        ConcatClearButton.IsEnabled = !running;
        ConcatDestinationBrowseButton.IsEnabled = !running;
        ConcatDestinationBox.IsReadOnly = running;
        ConcatPaddingCheckBox.IsEnabled = !running;
        ConcatPaddingBytesBox.IsReadOnly = running;
        ConcatBoundaryCheckBox.IsEnabled = !running;
        ConcatFilesList.IsEnabled = !running;
    }

    private void AppendConcatLog(string message) => AppendLog(ConcatLogBox, message);

    // ISO2BIN tab (ISO2BIN)
}
