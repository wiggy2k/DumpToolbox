using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private CancellationTokenSource? _isoExtractorCts;

    private void InitializeIsoExtractorTab()
    {
        AppendIsoExtractorLog("Extracts the user-visible Joliet tree when present while retaining exact primary ISO9660 record identity, associated/duplicate payloads and both namespace mappings in the DIC-aware manifest.");
    }

    private async void IsoExtractImageBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose ISO/BIN source image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CD image") { Patterns = new[] { "*.iso", "*.bin", "*.img" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            IsoExtractImagePathBox.Text = path;
            if (string.IsNullOrWhiteSpace(IsoExtractOutputFolderBox.Text))
            {
                string parent = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
                string name = Path.GetFileNameWithoutExtension(path) + "_DumpToolbox_Extracted";
                IsoExtractOutputFolderBox.Text = Path.Combine(parent, name);
            }
        }
    }

    private async void IsoExtractOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose extraction folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            IsoExtractOutputFolderBox.Text = path;
    }

    private async void IsoExtractStartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isoExtractorCts is not null)
            return;

        string image = IsoExtractImagePathBox.Text?.Trim() ?? string.Empty;
        string output = IsoExtractOutputFolderBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(output))
        {
            AppendIsoExtractorLog("ERROR: Choose both a source image and output folder.");
            return;
        }

        try
        {
            _isoExtractorCts = new CancellationTokenSource();
            SetIsoExtractorRunning(true);
            IsoExtractProgressBar.Value = 0;
            IsoExtractProgressText.Text = "Reading image...";
            AppendIsoExtractorLog($"Source: {image}");
            AppendIsoExtractorLog($"Output: {output}");

            var progress = new Progress<DicDonorProgress>(p =>
            {
                IsoExtractProgressBar.Value = p.Fraction * 100;
                IsoExtractProgressText.Text = p.Message;
            });

            IsoExtractionResult result = await _dicDonorImageService.ExtractAllAsync(
                image,
                output,
                progress,
                _isoExtractorCts.Token);

            IsoExtractProgressBar.Value = 100;
            IsoExtractProgressText.Text = "Complete";
            AppendIsoExtractorLog($"Extracted {result.FilesExtracted:N0} ISO file record(s).");
            AppendIsoExtractorLog(result.HasJoliet
                ? $"Visible namespace: Joliet ({result.JolietMappedRecords:N0} primary record(s) mapped explicitly)."
                : "Visible namespace: primary ISO9660 (no Joliet SVD detected).");
            AppendIsoExtractorLog($"Associated records preserved: {result.AssociatedFilesExtracted:N0}.");
            AppendIsoExtractorLog($"Additional colliding records preserved: {result.DuplicateRecordsPreserved:N0}.");
            AppendIsoExtractorLog($"Manifest: {result.ManifestPath}");
            foreach (string warning in result.Warnings)
                AppendIsoExtractorLog("WARNING: " + warning);

            DicSourceFolderBox.Text = result.OutputDirectory;
            AppendIsoExtractorLog("DIC Source Folder has been set to this extraction folder.");
        }
        catch (OperationCanceledException)
        {
            IsoExtractProgressText.Text = "Cancelled";
            AppendIsoExtractorLog("Extraction cancelled.");
        }
        catch (Exception ex)
        {
            IsoExtractProgressText.Text = "Error";
            AppendIsoExtractorLog("ERROR: " + ex.Message);
            await ShowMessageAsync("DumpToolbox — ISO Extractor", ex.Message);
        }
        finally
        {
            _isoExtractorCts?.Dispose();
            _isoExtractorCts = null;
            SetIsoExtractorRunning(false);
        }
    }

    private void IsoExtractCancelButton_Click(object? sender, RoutedEventArgs e)
        => _isoExtractorCts?.Cancel();

    private void SetIsoExtractorRunning(bool running)
    {
        IsoExtractImageBrowseButton.IsEnabled = !running;
        IsoExtractOutputBrowseButton.IsEnabled = !running;
        IsoExtractStartButton.IsEnabled = !running;
        IsoExtractCancelButton.IsEnabled = running;
        IsoExtractImagePathBox.IsReadOnly = running;
        IsoExtractOutputFolderBox.IsReadOnly = running;
    }

    private void AppendIsoExtractorLog(string message) => AppendLog(IsoExtractLogBox, message);
}
