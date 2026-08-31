using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly FindEndsService _findEndsService = new();
    private CancellationTokenSource? _findEndsCts;


    private async void FindEndsPartialBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose partial file",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            FindEndsPartialBox.Text = path;
            if (string.IsNullOrWhiteSpace(FindEndsOutputBox.Text))
                FindEndsOutputBox.Text = SuggestFindEndsOutputPath(path);
        }
    }

    private async void FindEndsSourceBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose file to search for the missing segment",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            FindEndsSourceBox.Text = path;
    }

    private void FindEndsSourceClearButton_Click(object? sender, RoutedEventArgs e)
    {
        FindEndsSourceBox.Text = string.Empty;
    }

    private async void FindEndsOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(FindEndsPartialBox.Text)
            ? "recovered.bin"
            : Path.GetFileName(SuggestFindEndsOutputPath(FindEndsPartialBox.Text!));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose recovered output file",
            SuggestedFileName = suggested
        });

        if (file?.TryGetLocalPath() is { } path)
            FindEndsOutputBox.Text = path;
    }

    private async void FindEndsStartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_findEndsCts is not null)
            return;

        try
        {
            string partial = FindEndsPartialBox.Text?.Trim() ?? string.Empty;

            IReadOnlyList<HashTarget> fullTargets = TargetParser.Parse(FindEndsTargetBox.Text ?? string.Empty);
            if (fullTargets.Count != 1)
                throw new InvalidOperationException($"Find-ends requires exactly one full-file target; {fullTargets.Count:N0} targets were found.");

            HashTarget fullTarget = fullTargets[0];
            if (string.IsNullOrWhiteSpace(fullTarget.Md5))
                throw new InvalidOperationException("The full-file target must include an MD5. Use a Redump track/file entry or: SIZE CRC32 MD5.");

            long fullLength = fullTarget.Size;
            uint fullCrc = fullTarget.Crc32;
            string md5 = fullTarget.Md5;

            FindEndsMode mode = FindEndsModeBox.SelectedIndex switch
            {
                1 => FindEndsMode.MissingStart,
                2 => FindEndsMode.MissingEnd,
                _ => FindEndsMode.Auto
            };

            string? source = string.IsNullOrWhiteSpace(FindEndsSourceBox.Text) ? null : FindEndsSourceBox.Text!.Trim();
            string? output = string.IsNullOrWhiteSpace(FindEndsOutputBox.Text) ? null : FindEndsOutputBox.Text!.Trim();

            _findEndsCts = new CancellationTokenSource();
            SetFindEndsRunning(true);
            FindEndsLogBox.Text = string.Empty;
            FindEndsProgressBar.Value = 0;
            FindEndsProgressText.Text = "Starting...";
            AppendFindEndsLog($"Partial: {partial}");
            AppendFindEndsLog($"Expected full file: {fullLength:N0} bytes | CRC32 {fullCrc:x8} | MD5 {md5.ToLowerInvariant()}");

            long lastLoggedOffset = -1;
            var progress = new Progress<FindEndsProgress>(p =>
            {
                FindEndsProgressBar.Value = p.Fraction * 100;
                FindEndsProgressText.Text = p.SearchableOffsets > 0
                    ? $"{p.Fraction:P1}  candidates {p.CrcCandidates:N0}"
                    : p.Message;
                SetWindowStatus($"Find-ends — {p.Message}");

                if (p.CrcCandidates > 0 || lastLoggedOffset < 0 || p.Offset - lastLoggedOffset >= 256L * 1024 * 1024)
                {
                    AppendFindEndsLog(p.Message);
                    lastLoggedOffset = p.Offset;
                }
            });

            FindEndsResult result = await _findEndsService.RunAsync(
                partial, fullLength, fullCrc, md5, mode, source, output, progress, _findEndsCts.Token);

            AppendFindEndsLog($"Partial size: {result.PartialLength:N0} bytes | CRC32 {result.PartialCrc32:x8}");
            foreach (FindEndsAnalysis analysis in result.Analyses)
                AppendFindEndsLog($"Need {analysis.MissingLength:N0} bytes at the {analysis.SideName} with CRC32 {analysis.MissingCrc32Hex}");

            if (!result.SourceSearched)
            {
                AppendFindEndsLog(result.Message ?? "Missing CRC calculated.");
                FindEndsProgressText.Text = "CRC calculated";
                FindEndsProgressBar.Value = 100;
            }
            else if (result.Found)
            {
                string matchedSide = result.MatchedMode == FindEndsMode.MissingStart ? "start" : "end";
                AppendFindEndsLog($"*** MATCH FOUND *** source offset {result.SourceOffset:N0}; missing {matchedSide}");
                AppendFindEndsLog($"MD5 verified: {result.VerifiedMd5}");
                AppendFindEndsLog($"Recovered file: {result.OutputPath}");
                if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    FindEndsOutputBox.Text = result.OutputPath;
                FindEndsProgressText.Text = "Recovered + MD5 verified";
                FindEndsProgressBar.Value = 100;
            }
            else
            {
                AppendFindEndsLog(result.Message ?? "No verified match found.");
                FindEndsProgressText.Text = "No verified match";
                FindEndsProgressBar.Value = 100;
            }

            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendFindEndsLog("Cancelled.");
            FindEndsProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendFindEndsLog($"ERROR: {ex.Message}");
            FindEndsProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — Find-ends", ex.Message);
        }
        finally
        {
            _findEndsCts?.Dispose();
            _findEndsCts = null;
            SetFindEndsRunning(false);
        }
    }

    private void FindEndsCancelButton_Click(object? sender, RoutedEventArgs e) => _findEndsCts?.Cancel();

    private void FindEndsClearButton_Click(object? sender, RoutedEventArgs e)
    {
        FindEndsPartialBox.Text = string.Empty;
        FindEndsTargetBox.Text = string.Empty;
        FindEndsSourceBox.Text = string.Empty;
        FindEndsOutputBox.Text = string.Empty;
        FindEndsLogBox.Text = string.Empty;
        FindEndsModeBox.SelectedIndex = 0;
        FindEndsProgressBar.Value = 0;
        FindEndsProgressText.Text = "Ready";
    }

    private void SetFindEndsRunning(bool running)
    {
        FindEndsPartialBrowseButton.IsEnabled = !running;
        FindEndsSourceBrowseButton.IsEnabled = !running;
        FindEndsSourceClearButton.IsEnabled = !running;
        FindEndsOutputBrowseButton.IsEnabled = !running;
        FindEndsStartButton.IsEnabled = !running;
        FindEndsCancelButton.IsEnabled = running;
        FindEndsClearButton.IsEnabled = !running;
        FindEndsModeBox.IsEnabled = !running;
        FindEndsPartialBox.IsReadOnly = running;
        FindEndsTargetBox.IsReadOnly = running;
        FindEndsSourceBox.IsReadOnly = running;
        FindEndsOutputBox.IsReadOnly = running;
    }

    private void AppendFindEndsLog(string message) => AppendLog(FindEndsLogBox, message);

    private static string SuggestFindEndsOutputPath(string partialPath)
    {
        string full = Path.GetFullPath(partialPath);
        string directory = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);
        return Path.Combine(directory, stem + "_fixed" + ext);
    }

}
