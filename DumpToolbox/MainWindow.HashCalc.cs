using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly HashCalculationService _hashCalculationService = new();
    private CancellationTokenSource? _hashCalcCts;


    private async void HashCalcBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose file to hash",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            HashCalcFileBox.Text = path;
    }

    private async void HashCalcStartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_hashCalcCts is not null)
            return;

        try
        {
            string filePath = HashCalcFileBox.Text?.Trim() ?? string.Empty;
            var options = new HashCalculationOptions(
                Crc32: HashCalcCrc32CheckBox.IsChecked == true,
                Md5: HashCalcMd5CheckBox.IsChecked == true,
                Sha1: HashCalcSha1CheckBox.IsChecked == true,
                Sha256: HashCalcSha256CheckBox.IsChecked == true,
                Sha384: HashCalcSha384CheckBox.IsChecked == true,
                Sha512: HashCalcSha512CheckBox.IsChecked == true);

            _hashCalcCts = new CancellationTokenSource();
            SetHashCalcRunning(true);
            HashCalcResultsBox.Text = string.Empty;
            HashCalcProgressBar.Value = 0;
            HashCalcProgressText.Text = "Starting...";

            var progress = new Progress<HashCalculationProgress>(p =>
            {
                HashCalcProgressBar.Value = p.Fraction * 100;
                HashCalcProgressText.Text = $"{p.Fraction:P1}  {p.BytesRead / 1048576.0:N1}/{p.TotalBytes / 1048576.0:N1} MiB";
                SetWindowStatus($"HashCalc — {p.Fraction:P0}");
            });

            HashCalculationResult result = await _hashCalculationService.CalculateAsync(
                filePath,
                options,
                progress,
                _hashCalcCts.Token);

            var text = new StringBuilder();
            text.AppendLine($"File: {result.FilePath}");
            text.AppendLine($"Size: {result.FileLength:N0} bytes");
            text.AppendLine();
            foreach ((string name, string value) in result.Hashes)
                text.AppendLine($"{name,-8} {value}");

            HashCalcResultsBox.Text = text.ToString().TrimEnd();
            HashCalcProgressBar.Value = 100;
            HashCalcProgressText.Text = "Complete";
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            HashCalcProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            HashCalcProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — HashCalc", ex.Message);
        }
        finally
        {
            _hashCalcCts?.Dispose();
            _hashCalcCts = null;
            SetHashCalcRunning(false);
        }
    }

    private void HashCalcCancelButton_Click(object? sender, RoutedEventArgs e) => _hashCalcCts?.Cancel();

    private void HashCalcClearButton_Click(object? sender, RoutedEventArgs e)
    {
        HashCalcResultsBox.Text = string.Empty;
        HashCalcProgressBar.Value = 0;
        HashCalcProgressText.Text = "Ready";
    }

    private void SetHashCalcRunning(bool running)
    {
        HashCalcBrowseButton.IsEnabled = !running;
        HashCalcStartButton.IsEnabled = !running;
        HashCalcCancelButton.IsEnabled = running;
        HashCalcClearButton.IsEnabled = !running;
        HashCalcFileBox.IsReadOnly = running;
        HashCalcCrc32CheckBox.IsEnabled = !running;
        HashCalcMd5CheckBox.IsEnabled = !running;
        HashCalcSha1CheckBox.IsEnabled = !running;
        HashCalcSha256CheckBox.IsEnabled = !running;
        HashCalcSha384CheckBox.IsEnabled = !running;
        HashCalcSha512CheckBox.IsEnabled = !running;
    }

}
