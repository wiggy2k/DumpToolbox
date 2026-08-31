using System.Diagnostics;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly Mdf2BinService _mdf2BinService = new();
    private CancellationTokenSource? _mdf2BinCts;
    private Mdf2BinInspection? _mdf2BinInspection;

    private async void Mdf2BinMdsBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Alcohol MDS descriptor",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Alcohol MDS descriptor") { Patterns = new[] { "*.mds" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        Mdf2BinMdsBox.Text = path;
        // Leave the MDF field empty so Analyse can honour an explicit filename from
        // the MDS footer before falling back to the matching-basename .mdf.
        Mdf2BinMdfBox.Text = string.Empty;

        string bin = Mdf2BinService.SuggestBinPath(path);
        Mdf2BinOutputBinBox.Text = bin;
        Mdf2BinOutputCueBox.Text = Mdf2BinService.SuggestCuePath(bin);
        _mdf2BinInspection = null;
        Mdf2BinInspectionText.Text = "MDS selected — click Analyse to inspect the disc layout.";
    }

    private async void Mdf2BinMdfBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Alcohol MDF data file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Alcohol MDF data") { Patterns = new[] { "*.mdf" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Mdf2BinMdfBox.Text = path;
            _mdf2BinInspection = null;
        }
    }

    private void Mdf2BinMdfAutoButton_Click(object? sender, RoutedEventArgs e)
    {
        Mdf2BinMdfBox.Text = string.Empty;
        _mdf2BinInspection = null;
        Mdf2BinInspectionText.Text = "MDF override cleared. Analyse will resolve the MDF from the MDS footer or matching basename.";
    }

    private async void Mdf2BinOutputBinBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(Mdf2BinOutputBinBox.Text)
            ? (string.IsNullOrWhiteSpace(Mdf2BinMdsBox.Text)
                ? "converted.bin"
                : Path.GetFileName(Mdf2BinService.SuggestBinPath(Mdf2BinMdsBox.Text!)))
            : Path.GetFileName(Mdf2BinOutputBinBox.Text!);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose MDF2BIN output BIN",
            SuggestedFileName = suggested,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Raw CD BIN") { Patterns = new[] { "*.bin" } }
            }
        });

        if (file?.TryGetLocalPath() is not { } path)
            return;

        Mdf2BinOutputBinBox.Text = path;
        if (string.IsNullOrWhiteSpace(Mdf2BinOutputCueBox.Text) ||
            Path.GetFileNameWithoutExtension(Mdf2BinOutputCueBox.Text!) != Path.GetFileNameWithoutExtension(path))
        {
            Mdf2BinOutputCueBox.Text = Mdf2BinService.SuggestCuePath(path);
        }
    }

    private async void Mdf2BinOutputCueBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(Mdf2BinOutputCueBox.Text)
            ? (string.IsNullOrWhiteSpace(Mdf2BinOutputBinBox.Text)
                ? "converted.cue"
                : Path.GetFileName(Mdf2BinService.SuggestCuePath(Mdf2BinOutputBinBox.Text!)))
            : Path.GetFileName(Mdf2BinOutputCueBox.Text!);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose MDF2BIN output CUE",
            SuggestedFileName = suggested,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CUE sheet") { Patterns = new[] { "*.cue" } }
            }
        });

        if (file?.TryGetLocalPath() is { } path)
            Mdf2BinOutputCueBox.Text = path;
    }

    private async void Mdf2BinAnalyzeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mdf2BinCts is not null)
            return;

        try
        {
            _mdf2BinCts = new CancellationTokenSource();
            SetMdf2BinRunning(true);
            Mdf2BinProgressBar.Value = 0;
            Mdf2BinProgressText.Text = "Analysing...";
            Mdf2BinLogBox.Text = string.Empty;

            Mdf2BinInspection inspection = await AnalyzeMdf2BinFromUiAsync(_mdf2BinCts.Token);
            _mdf2BinInspection = inspection;
            ApplyMdf2BinInspection(inspection);
            AppendMdf2BinLog($"MDS {inspection.VersionMajor}.{inspection.VersionMinor}; {inspection.MediumType}; {inspection.SessionCount} session(s); {inspection.Tracks.Count} track(s).");
            AppendMdf2BinLog($"MDF: {inspection.MdfPath}");
            AppendMdf2BinLog($"Output main channel: {inspection.OutputSectors:N0} sectors / {inspection.OutputBytes:N0} bytes.");
            foreach (string warning in inspection.Warnings)
                AppendMdf2BinLog($"WARNING: {warning}");

            Mdf2BinProgressText.Text = "Analysed";
        }
        catch (OperationCanceledException)
        {
            Mdf2BinProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            _mdf2BinInspection = null;
            Mdf2BinProgressText.Text = "Error";
            AppendMdf2BinLog($"ERROR: {ex.Message}");
            await ShowMessageAsync("DumpToolbox — MDF2BIN", ex.Message);
        }
        finally
        {
            _mdf2BinCts?.Dispose();
            _mdf2BinCts = null;
            SetMdf2BinRunning(false);
        }
    }

    private async void Mdf2BinConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mdf2BinCts is not null)
            return;

        try
        {
            string mds = Mdf2BinMdsBox.Text?.Trim() ?? string.Empty;
            string? mdf = string.IsNullOrWhiteSpace(Mdf2BinMdfBox.Text) ? null : Mdf2BinMdfBox.Text!.Trim();
            string outputBin = Mdf2BinOutputBinBox.Text?.Trim() ?? string.Empty;
            string outputCue = Mdf2BinOutputCueBox.Text?.Trim() ?? string.Empty;
            bool saveSub = Mdf2BinSaveSubCheckBox.IsChecked == true;

            if (string.IsNullOrWhiteSpace(mds))
                throw new InvalidOperationException("Choose an MDS descriptor file.");
            if (string.IsNullOrWhiteSpace(outputBin))
                throw new InvalidOperationException("Choose an output BIN filename.");
            if (string.IsNullOrWhiteSpace(outputCue))
            {
                outputCue = Mdf2BinService.SuggestCuePath(outputBin);
                Mdf2BinOutputCueBox.Text = outputCue;
            }

            _mdf2BinCts = new CancellationTokenSource();
            SetMdf2BinRunning(true);
            Mdf2BinLogBox.Text = string.Empty;
            Mdf2BinProgressBar.Value = 0;
            Mdf2BinProgressText.Text = "Analysing...";

            Mdf2BinInspection inspection = await _mdf2BinService.AnalyzeAsync(mds, mdf, _mdf2BinCts.Token);
            _mdf2BinInspection = inspection;
            ApplyMdf2BinInspection(inspection);
            Mdf2BinMdfBox.Text = inspection.MdfPath;

            AppendMdf2BinLog($"MDS: {inspection.MdsPath}");
            AppendMdf2BinLog($"MDF: {inspection.MdfPath}");
            AppendMdf2BinLog($"Descriptor: MDS {inspection.VersionMajor}.{inspection.VersionMinor}; {inspection.MediumType}; {inspection.SessionCount} session(s); {inspection.Tracks.Count} track(s).");
            foreach (string warning in inspection.Warnings)
                AppendMdf2BinLog($"WARNING: {warning}");

            var stopwatch = Stopwatch.StartNew();
            long lastLogged = -1;
            var progress = new Progress<Mdf2BinProgress>(p =>
            {
                Mdf2BinProgressBar.Value = p.Fraction * 100;
                double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double mibPerSecond = p.InputBytesProcessed / 1048576.0 / seconds;
                Mdf2BinProgressText.Text = $"{p.Fraction:P1}  {mibPerSecond:N1} MiB/s";
                SetWindowStatus($"MDF2BIN — {p.Fraction:P0}");

                long interval = Math.Max(1, p.TotalSectors / 4);
                if (lastLogged < 0 || p.SectorsProcessed - lastLogged >= interval || p.SectorsProcessed == p.TotalSectors)
                {
                    AppendMdf2BinLog($"Progress: {p.Fraction:P1} | {p.SectorsProcessed:N0} / {p.TotalSectors:N0} sectors | {mibPerSecond:N1} MiB/s input");
                    lastLogged = p.SectorsProcessed;
                }
            });

            var activity = new Progress<string>(AppendMdf2BinLog);
            Mdf2BinResult result = await _mdf2BinService.ConvertAsync(
                mds,
                mdf,
                outputBin,
                outputCue,
                saveSub,
                progress,
                activity,
                _mdf2BinCts.Token);

            stopwatch.Stop();
            Mdf2BinProgressBar.Value = 100;
            Mdf2BinProgressText.Text = $"Complete — {result.SectorCount:N0} sectors";
            AppendMdf2BinLog($"Complete in {stopwatch.Elapsed}.");
            AppendMdf2BinLog($"BIN: {result.OutputBinPath} ({result.OutputBytes:N0} bytes)");
            AppendMdf2BinLog($"CUE: {result.OutputCuePath}");
            if (result.OutputSubPath is not null)
                AppendMdf2BinLog($"SUB: {result.OutputSubPath}");

            if (Mdf2BinSendToFindCrcsCheckBox.IsChecked == true)
            {
                AppendMdf2BinLog($"FindCRCs source set to: {result.OutputBinPath}");
                SendBinToFindCrcs(result.OutputBinPath);
            }

            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            Mdf2BinProgressText.Text = "Cancelled";
            AppendMdf2BinLog("Conversion cancelled. Partial output removed.");
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            Mdf2BinProgressText.Text = "Error";
            AppendMdf2BinLog($"ERROR: {ex.Message}");
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — MDF2BIN", ex.Message);
        }
        finally
        {
            _mdf2BinCts?.Dispose();
            _mdf2BinCts = null;
            SetMdf2BinRunning(false);
        }
    }

    private void Mdf2BinCancelButton_Click(object? sender, RoutedEventArgs e) => _mdf2BinCts?.Cancel();

    private async Task<Mdf2BinInspection> AnalyzeMdf2BinFromUiAsync(CancellationToken cancellationToken)
    {
        string mds = Mdf2BinMdsBox.Text?.Trim() ?? string.Empty;
        string? mdf = string.IsNullOrWhiteSpace(Mdf2BinMdfBox.Text) ? null : Mdf2BinMdfBox.Text!.Trim();
        Mdf2BinInspection inspection = await _mdf2BinService.AnalyzeAsync(mds, mdf, cancellationToken);
        Mdf2BinMdfBox.Text = inspection.MdfPath;

        if (string.IsNullOrWhiteSpace(Mdf2BinOutputBinBox.Text))
            Mdf2BinOutputBinBox.Text = Mdf2BinService.SuggestBinPath(mds);
        if (string.IsNullOrWhiteSpace(Mdf2BinOutputCueBox.Text))
            Mdf2BinOutputCueBox.Text = Mdf2BinService.SuggestCuePath(Mdf2BinOutputBinBox.Text!);

        return inspection;
    }

    private void ApplyMdf2BinInspection(Mdf2BinInspection inspection)
    {
        var text = new StringBuilder();
        text.AppendLine($"MDS {inspection.VersionMajor}.{inspection.VersionMinor}   Medium: {inspection.MediumType}   Sessions: {inspection.SessionCount}   Tracks: {inspection.Tracks.Count}");
        text.AppendLine($"MDF: {inspection.MdfPath}");
        text.AppendLine($"BIN: {inspection.OutputSectors:N0} sectors × 2352 = {inspection.OutputBytes:N0} bytes");
        text.AppendLine();
        text.AppendLine("Trk Ses Mode               LBA        Data sectors   Pregap(rep/stored) MDF INDEX01       BIN INDEX01  Sub");
        text.AppendLine("--- --- ------------------ ---------- -------------- ------------------ ----------------- ----------- ----");

        foreach (Mdf2BinTrackInspection track in inspection.Tracks)
        {
            text.AppendLine(
                $"{track.Number,2:00}  {track.Session,2}  {track.Mode,-18} {track.StartLba,10:N0} {track.DataSectors,14:N0} " +
                $"{track.ReportedPregapSectors,7:N0}/{track.StoredPregapSectors,-7:N0} {track.MdfIndex01Offset,17:N0} {FormatMdf2BinCueTime(track.OutputIndex01Sector),11} " +
                $"{(track.HasInterleavedSubchannel ? "96B" : "-")}");
        }

        if (inspection.Warnings.Count > 0)
        {
            text.AppendLine();
            foreach (string warning in inspection.Warnings)
                text.AppendLine($"WARNING: {warning}");
        }

        Mdf2BinInspectionText.Text = text.ToString().TrimEnd();
        Mdf2BinSaveSubCheckBox.IsEnabled = _mdf2BinCts is null && inspection.AllTracksHaveInterleavedSubchannel;
        if (!inspection.AllTracksHaveInterleavedSubchannel)
            Mdf2BinSaveSubCheckBox.IsChecked = false;
    }

    private void SetMdf2BinRunning(bool running)
    {
        Mdf2BinMdsBrowseButton.IsEnabled = !running;
        Mdf2BinMdfBrowseButton.IsEnabled = !running;
        Mdf2BinMdfAutoButton.IsEnabled = !running;
        Mdf2BinOutputBinBrowseButton.IsEnabled = !running;
        Mdf2BinOutputCueBrowseButton.IsEnabled = !running;
        Mdf2BinAnalyzeButton.IsEnabled = !running;
        Mdf2BinConvertButton.IsEnabled = !running;
        Mdf2BinCancelButton.IsEnabled = running;
        Mdf2BinMdsBox.IsReadOnly = running;
        Mdf2BinMdfBox.IsReadOnly = running;
        Mdf2BinOutputBinBox.IsReadOnly = running;
        Mdf2BinOutputCueBox.IsReadOnly = running;
        Mdf2BinSaveSubCheckBox.IsEnabled = !running && (_mdf2BinInspection?.AllTracksHaveInterleavedSubchannel ?? true);
        Mdf2BinSendToFindCrcsCheckBox.IsEnabled = !running;
    }

    private void AppendMdf2BinLog(string message) => AppendLog(Mdf2BinLogBox, message);

    private static string FormatMdf2BinCueTime(long sectors)
    {
        long minutes = sectors / (75 * 60);
        long remainder = sectors % (75 * 60);
        return $"{minutes:00}:{remainder / 75:00}:{remainder % 75:00}";
    }
}
