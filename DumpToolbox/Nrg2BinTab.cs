using System.Diagnostics;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly Nrg2BinService _nrg2BinService = new();
    private CancellationTokenSource? _nrg2BinCts;
    private Nrg2BinInspection? _nrg2BinInspection;

    private async void Nrg2BinInputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Nero NRG image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Nero NRG image") { Patterns = new[] { "*.nrg" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        Nrg2BinInputBox.Text = path;
        string bin = Nrg2BinService.SuggestBinPath(path);
        Nrg2BinOutputBinBox.Text = bin;
        Nrg2BinOutputCueBox.Text = Nrg2BinService.SuggestCuePath(bin);
        ResetNrg2BinMediaUi();
        _nrg2BinInspection = null;
        Nrg2BinInspectionText.Text = "NRG selected — click Analyse to inspect the footer, chunks and track layout.";
    }

    private async void Nrg2BinOutputBinBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        bool dvd = _nrg2BinInspection?.IsDvd == true;
        string suggested = string.IsNullOrWhiteSpace(Nrg2BinOutputBinBox.Text)
            ? (string.IsNullOrWhiteSpace(Nrg2BinInputBox.Text) ? (dvd ? "converted.iso" : "converted.bin") : Path.GetFileName(dvd ? Nrg2BinService.SuggestIsoPath(Nrg2BinInputBox.Text!) : Nrg2BinService.SuggestBinPath(Nrg2BinInputBox.Text!)))
            : Path.GetFileName(Nrg2BinOutputBinBox.Text!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = dvd ? "Choose NRG DVD ISO output" : "Choose NRG CD BIN output", SuggestedFileName = suggested,
            FileTypeChoices = new[] { dvd ? new FilePickerFileType("DVD ISO") { Patterns = new[] { "*.iso" } } : new FilePickerFileType("Raw CD BIN") { Patterns = new[] { "*.bin" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Nrg2BinOutputBinBox.Text = dvd ? Path.ChangeExtension(path, ".iso") : Path.ChangeExtension(path, ".bin");
        if (!dvd && (string.IsNullOrWhiteSpace(Nrg2BinOutputCueBox.Text) || Path.GetFileNameWithoutExtension(Nrg2BinOutputCueBox.Text!) != Path.GetFileNameWithoutExtension(path)))
            Nrg2BinOutputCueBox.Text = Nrg2BinService.SuggestCuePath(path);
    }

    private async void Nrg2BinOutputCueBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(Nrg2BinOutputCueBox.Text)
            ? (string.IsNullOrWhiteSpace(Nrg2BinOutputBinBox.Text) ? "converted.cue" : Path.GetFileName(Nrg2BinService.SuggestCuePath(Nrg2BinOutputBinBox.Text!)))
            : Path.GetFileName(Nrg2BinOutputCueBox.Text!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose NRG2BIN output CUE",
            SuggestedFileName = suggested,
            FileTypeChoices = new[] { new FilePickerFileType("CUE sheet") { Patterns = new[] { "*.cue" } } }
        });
        if (file?.TryGetLocalPath() is { } path)
            Nrg2BinOutputCueBox.Text = path;
    }

    private async void Nrg2BinAnalyzeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_nrg2BinCts is not null) return;
        try
        {
            _nrg2BinCts = new CancellationTokenSource();
            SetNrg2BinRunning(true);
            Nrg2BinProgressBar.Value = 0;
            Nrg2BinProgressText.Text = "Analysing...";
            Nrg2BinLogBox.Text = string.Empty;
            Nrg2BinInspection inspection = await AnalyzeNrg2BinFromUiAsync(_nrg2BinCts.Token);
            _nrg2BinInspection = inspection;
            ApplyNrg2BinInspection(inspection);
            AppendNrg2BinLog($"{inspection.FooterId} / NRG v{inspection.FormatVersion}; {inspection.RecordingMode}; {inspection.SessionCount} session(s); {inspection.Tracks.Count} track(s).");
            foreach (string warning in inspection.Warnings) AppendNrg2BinLog($"WARNING: {warning}");
            Nrg2BinProgressText.Text = "Analysed";
        }
        catch (OperationCanceledException) { Nrg2BinProgressText.Text = "Cancelled"; }
        catch (Exception ex)
        {
            _nrg2BinInspection = null;
            Nrg2BinProgressText.Text = "Error";
            AppendNrg2BinLog($"ERROR: {ex.Message}");
            await ShowMessageAsync("DumpToolbox — NRG2BIN", ex.Message);
        }
        finally
        {
            _nrg2BinCts?.Dispose(); _nrg2BinCts = null; SetNrg2BinRunning(false);
        }
    }

    private async void Nrg2BinConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_nrg2BinCts is not null) return;
        try
        {
            string input = Nrg2BinInputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("Choose an NRG image.");

            _nrg2BinCts = new CancellationTokenSource();
            SetNrg2BinRunning(true);
            Nrg2BinLogBox.Text = string.Empty;
            Nrg2BinProgressBar.Value = 0;
            Nrg2BinProgressText.Text = "Analysing...";

            Nrg2BinInspection inspection = await _nrg2BinService.AnalyzeAsync(input, _nrg2BinCts.Token);
            _nrg2BinInspection = inspection;
            ApplyNrg2BinInspection(inspection);
            string outputBin = Nrg2BinOutputBinBox.Text?.Trim() ?? string.Empty;
            string outputCue = Nrg2BinOutputCueBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outputBin)) outputBin = inspection.IsDvd ? Nrg2BinService.SuggestIsoPath(input) : Nrg2BinService.SuggestBinPath(input);
            if (inspection.IsDvd) { outputBin = Path.ChangeExtension(outputBin, ".iso"); outputCue = string.Empty; }
            else if (string.IsNullOrWhiteSpace(outputCue)) outputCue = Nrg2BinService.SuggestCuePath(outputBin);
            Nrg2BinOutputBinBox.Text = outputBin; Nrg2BinOutputCueBox.Text = outputCue;
            foreach (string warning in inspection.Warnings) AppendNrg2BinLog($"WARNING: {warning}");

            var sw = Stopwatch.StartNew();
            var progress = new Progress<Nrg2BinProgress>(p =>
            {
                Nrg2BinProgressBar.Value = p.Fraction * 100;
                double rate = p.InputBytesProcessed / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                Nrg2BinProgressText.Text = $"{p.Fraction:P1}  {rate:N1} MiB/s";
                SetWindowStatus($"NRG2BIN — {p.Fraction:P0}");
            });
            var activity = new Progress<string>(AppendNrg2BinLog);
            Nrg2BinResult result = await _nrg2BinService.ConvertAsync(input, outputBin, outputCue, Nrg2BinSaveSubCheckBox.IsChecked == true, progress, activity, _nrg2BinCts.Token);
            sw.Stop();
            Nrg2BinProgressBar.Value = 100;
            Nrg2BinProgressText.Text = $"Complete — {result.SectorCount:N0} sectors";
            AppendNrg2BinLog($"Complete in {sw.Elapsed}.");
            AppendNrg2BinLog($"{(inspection.IsDvd ? "ISO" : "BIN")}: {result.OutputBinPath} ({result.OutputBytes:N0} bytes)");
            if (!inspection.IsDvd) AppendNrg2BinLog($"CUE: {result.OutputCuePath}");
            if (result.OutputSubPath is not null)
                AppendNrg2BinLog($"SUB: {result.OutputSubPath}");
            if (!inspection.IsDvd && Nrg2BinSendToFindCrcsCheckBox.IsChecked == true)
            {
                AppendNrg2BinLog($"FindCRCs source set to: {result.OutputBinPath}");
                SendBinToFindCrcs(result.OutputBinPath);
            }
            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            Nrg2BinProgressText.Text = "Cancelled";
            AppendNrg2BinLog("Conversion cancelled. Partial output removed.");
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            Nrg2BinProgressText.Text = "Error";
            AppendNrg2BinLog($"ERROR: {ex.Message}");
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — NRG2BIN", ex.Message);
        }
        finally
        {
            _nrg2BinCts?.Dispose(); _nrg2BinCts = null; SetNrg2BinRunning(false);
        }
    }

    private void Nrg2BinCancelButton_Click(object? sender, RoutedEventArgs e) => _nrg2BinCts?.Cancel();

    private async Task<Nrg2BinInspection> AnalyzeNrg2BinFromUiAsync(CancellationToken token)
    {
        string input = Nrg2BinInputBox.Text?.Trim() ?? string.Empty;
        Nrg2BinInspection inspection = await _nrg2BinService.AnalyzeAsync(input, token);
        if (inspection.IsDvd) { Nrg2BinOutputBinBox.Text = Nrg2BinService.SuggestIsoPath(input); Nrg2BinOutputCueBox.Text = string.Empty; }
        else { Nrg2BinOutputBinBox.Text = Nrg2BinService.SuggestBinPath(input); Nrg2BinOutputCueBox.Text = Nrg2BinService.SuggestCuePath(Nrg2BinOutputBinBox.Text!); }
        return inspection;
    }

    private void ApplyNrg2BinInspection(Nrg2BinInspection inspection)
    {
        Nrg2BinOutputImageLabel.Text = inspection.IsDvd ? "Output ISO" : "Output BIN";
        Nrg2BinOutputCueLabel.IsVisible = !inspection.IsDvd; Nrg2BinOutputCueBox.IsVisible = !inspection.IsDvd; Nrg2BinOutputCueBrowseButton.IsVisible = !inspection.IsDvd;
        Nrg2BinSaveSubCheckBox.IsVisible = !inspection.IsDvd; Nrg2BinSendToFindCrcsCheckBox.IsVisible = !inspection.IsDvd;
        Nrg2BinOutputBinBox.Watermark = inspection.IsDvd ? "2048-byte DVD ISO output..." : "Raw 2352-byte BIN output...";
        var text = new StringBuilder();
        text.AppendLine($"NRG v{inspection.FormatVersion} ({inspection.FooterId})   Mode: {inspection.RecordingMode}   Sessions: {inspection.SessionCount}   Tracks: {inspection.Tracks.Count}");
        text.AppendLine($"Chunk chain: 0x{inspection.ChunkChainOffset:X}   Chunks: {string.Join(", ", inspection.Chunks.Select(c => c.Id))}");
        text.AppendLine($"Media: {(inspection.IsDvd ? "DVD" : "CD")}   Output: {(inspection.IsDvd ? $"ISO: {inspection.OutputSectors:N0} sectors × 2048" : $"BIN/CUE: {inspection.OutputSectors:N0} sectors × 2352")} = {inspection.OutputBytes:N0} bytes");
        if (inspection.MediaTypeValue.HasValue) text.AppendLine($"NRG MTYP: 0x{inspection.MediaTypeValue.Value:X}");
        if (!inspection.IsDvd) text.AppendLine($"Subchannel: {(inspection.HasSubchannel ? (Nrg2BinSaveSubCheckBox.IsChecked == true ? "stored on one or more tracks → .sub output enabled" : "stored on one or more tracks → .sub output available (disabled)") : "none stored")}");
        text.AppendLine();
        text.AppendLine("Ses Trk Type                         Stored  Sub  Sectors      Disc LBA   NRG offset          BIN INDEX01");
        text.AppendLine("--- --- ---------------------------- ------- ---- ------------ ---------- ------------------- -----------");
        foreach (NrgTrackInspection t in inspection.Tracks)
            text.AppendLine($"{t.SessionNumber,3:00} {t.Number,3:00} {DescribeNrgKind(t.Kind),-28} {t.StoredSectorSize,7} {(t.HasSubchannel ? "yes" : "no"),4} {t.SectorCount,12:N0} {t.DiscIndex01Lba,10:N0} 0x{t.SourceOffset,16:X} {FormatNrgCueTime(t.OutputIndex01Sector),11}");
        if (inspection.Warnings.Count > 0)
        {
            text.AppendLine();
            foreach (string warning in inspection.Warnings) text.AppendLine($"WARNING: {warning}");
        }
        Nrg2BinInspectionText.Text = text.ToString().TrimEnd();
    }

    private static string DescribeNrgKind(NrgTrackKind kind) => kind switch
    {
        NrgTrackKind.Audio => "Audio",
        NrgTrackKind.Mode1Cooked => "Mode 1 cooked → raw",
        NrgTrackKind.Mode1Raw => "Mode 1 raw",
        NrgTrackKind.Mode2Cooked => "Mode 2 Form 1 cooked → raw",
        NrgTrackKind.Mode2Raw => "Mode 2 raw",
        _ => kind.ToString()
    };

    private void ResetNrg2BinMediaUi()
    {
        Nrg2BinOutputImageLabel.Text = "Output image"; Nrg2BinOutputBinBox.Watermark = "BIN for CD / ISO for DVD...";
        Nrg2BinOutputCueLabel.IsVisible = true; Nrg2BinOutputCueBox.IsVisible = true; Nrg2BinOutputCueBrowseButton.IsVisible = true;
        Nrg2BinSaveSubCheckBox.IsVisible = true; Nrg2BinSendToFindCrcsCheckBox.IsVisible = true;
    }

    private void SetNrg2BinRunning(bool running)
    {
        Nrg2BinInputBrowseButton.IsEnabled = !running;
        Nrg2BinOutputBinBrowseButton.IsEnabled = !running;
        Nrg2BinOutputCueBrowseButton.IsEnabled = !running;
        Nrg2BinAnalyzeButton.IsEnabled = !running;
        Nrg2BinConvertButton.IsEnabled = !running;
        Nrg2BinCancelButton.IsEnabled = running;
        Nrg2BinInputBox.IsReadOnly = running;
        Nrg2BinOutputBinBox.IsReadOnly = running;
        Nrg2BinOutputCueBox.IsReadOnly = running;
        Nrg2BinSaveSubCheckBox.IsEnabled = !running;
        Nrg2BinSendToFindCrcsCheckBox.IsEnabled = !running;
    }

    private void AppendNrg2BinLog(string message) => AppendLog(Nrg2BinLogBox, message);
    private static string FormatNrgCueTime(long sectors) => $"{sectors / 4500:00}:{(sectors / 75) % 60:00}:{sectors % 75:00}";
}
