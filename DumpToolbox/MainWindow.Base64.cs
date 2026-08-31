using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly Base64Service _base64Service = new();
    private CancellationTokenSource? _base64Cts;


    private void Base64Mode_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateBase64ModeUi();

    private void UpdateBase64ModeUi()
    {
        bool fileMode = Base64InputTypeBox.SelectedIndex == 1;
        Base64TextPanel.IsVisible = !fileMode;
        Base64FilePanel.IsVisible = fileMode;

        if (fileMode && !string.IsNullOrWhiteSpace(Base64InputFileBox.Text) && string.IsNullOrWhiteSpace(Base64OutputFileBox.Text))
            Base64OutputFileBox.Text = SuggestBase64OutputPath(Base64InputFileBox.Text!, Base64OperationBox.SelectedIndex == 1);
    }

    private async void Base64InputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Base64OperationBox.SelectedIndex == 1 ? "Choose Base64 text file to decode" : "Choose file to Base64 encode",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Base64InputFileBox.Text = path;
            Base64OutputFileBox.Text = SuggestBase64OutputPath(path, Base64OperationBox.SelectedIndex == 1);
        }
    }

    private async void Base64OutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(Base64InputFileBox.Text)
            ? (Base64OperationBox.SelectedIndex == 1 ? "decoded.bin" : "encoded.b64")
            : Path.GetFileName(SuggestBase64OutputPath(Base64InputFileBox.Text!, Base64OperationBox.SelectedIndex == 1));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose Base64 output file",
            SuggestedFileName = suggested
        });

        if (file?.TryGetLocalPath() is { } path)
            Base64OutputFileBox.Text = path;
    }

    private async void Base64ConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_base64Cts is not null)
            return;

        try
        {
            bool decode = Base64OperationBox.SelectedIndex == 1;
            bool fileMode = Base64InputTypeBox.SelectedIndex == 1;
            _base64Cts = new CancellationTokenSource();
            SetBase64Running(true);
            Base64StatusText.Text = decode ? "Decoding..." : "Encoding...";

            if (!fileMode)
            {
                string input = Base64InputTextBox.Text ?? string.Empty;
                Base64OutputTextBox.Text = decode
                    ? Base64Service.DecodeText(input)
                    : Base64Service.EncodeText(input);
            }
            else
            {
                string input = Base64InputFileBox.Text?.Trim() ?? string.Empty;
                string output = Base64OutputFileBox.Text?.Trim() ?? string.Empty;
                if (decode)
                    await _base64Service.DecodeFileAsync(input, output, _base64Cts.Token);
                else
                    await _base64Service.EncodeFileAsync(input, output, _base64Cts.Token);
            }

            Base64StatusText.Text = "Complete";
        }
        catch (OperationCanceledException)
        {
            Base64StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            Base64StatusText.Text = "Error";
            await ShowMessageAsync("DumpToolbox — Base64", ex.Message);
        }
        finally
        {
            _base64Cts?.Dispose();
            _base64Cts = null;
            SetBase64Running(false);
        }
    }

    private void Base64CancelButton_Click(object? sender, RoutedEventArgs e) => _base64Cts?.Cancel();

    private void Base64ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        Base64InputTextBox.Text = string.Empty;
        Base64OutputTextBox.Text = string.Empty;
        Base64InputFileBox.Text = string.Empty;
        Base64OutputFileBox.Text = string.Empty;
        Base64StatusText.Text = "Ready";
    }

    private void SetBase64Running(bool running)
    {
        Base64OperationBox.IsEnabled = !running;
        Base64InputTypeBox.IsEnabled = !running;
        Base64InputTextBox.IsReadOnly = running;
        Base64InputFileBox.IsReadOnly = running;
        Base64OutputFileBox.IsReadOnly = running;
        Base64InputBrowseButton.IsEnabled = !running;
        Base64OutputBrowseButton.IsEnabled = !running;
        Base64ConvertButton.IsEnabled = !running;
        Base64CancelButton.IsEnabled = running;
        Base64ClearButton.IsEnabled = !running;
    }

    private static string SuggestBase64OutputPath(string inputPath, bool decode)
    {
        string full = Path.GetFullPath(inputPath);
        string directory = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        string filename = Path.GetFileName(full);

        if (!decode)
            return Path.Combine(directory, filename + ".b64");

        string ext = Path.GetExtension(filename);
        if (ext.Equals(".b64", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".base64", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(filename);
            if (!string.IsNullOrWhiteSpace(stem))
                return Path.Combine(directory, stem);
        }

        return Path.Combine(directory, filename + ".decoded");
    }


}
