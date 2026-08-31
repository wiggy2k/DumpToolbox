using System.Diagnostics;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private readonly Cdi2BinService _cdi2BinService = new();
    private CancellationTokenSource? _cdi2BinCts;
    private Cdi2BinInspection? _cdi2BinInspection;

    private async void Cdi2BinInputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose DiscJuggler CDI image", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("DiscJuggler CDI image") { Patterns = new[] { "*.cdi" } }, FilePickerFileTypes.All }
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        Cdi2BinInputBox.Text = path;
        string bin = Cdi2BinService.SuggestBinPath(path);
        Cdi2BinOutputBinBox.Text = bin;
        Cdi2BinOutputCueBox.Text = Cdi2BinService.SuggestCuePath(bin);
        ResetCdi2BinMediaUi();
        _cdi2BinInspection = null;
        Cdi2BinInspectionText.Text = "CDI selected — click Analyse to inspect sessions, tracks and sector storage.";
    }

    private async void Cdi2BinOutputBinBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        bool dvd = _cdi2BinInspection?.IsDvd == true;
        string suggested = string.IsNullOrWhiteSpace(Cdi2BinOutputBinBox.Text)
            ? (string.IsNullOrWhiteSpace(Cdi2BinInputBox.Text) ? (dvd ? "converted.iso" : "converted.bin") : Path.GetFileName(dvd ? Cdi2BinService.SuggestIsoPath(Cdi2BinInputBox.Text!) : Cdi2BinService.SuggestBinPath(Cdi2BinInputBox.Text!)))
            : Path.GetFileName(Cdi2BinOutputBinBox.Text!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = dvd ? "Choose CDI DVD ISO output" : "Choose CDI CD BIN output", SuggestedFileName = suggested,
            FileTypeChoices = new[] { dvd ? new FilePickerFileType("DVD ISO") { Patterns = new[] { "*.iso" } } : new FilePickerFileType("Raw CD BIN") { Patterns = new[] { "*.bin" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Cdi2BinOutputBinBox.Text = dvd ? Path.ChangeExtension(path, ".iso") : Path.ChangeExtension(path, ".bin");
        if (!dvd && (string.IsNullOrWhiteSpace(Cdi2BinOutputCueBox.Text) || Path.GetFileNameWithoutExtension(Cdi2BinOutputCueBox.Text!) != Path.GetFileNameWithoutExtension(path)))
            Cdi2BinOutputCueBox.Text = Cdi2BinService.SuggestCuePath(path);
    }

    private async void Cdi2BinOutputCueBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(Cdi2BinOutputCueBox.Text)
            ? (string.IsNullOrWhiteSpace(Cdi2BinOutputBinBox.Text) ? "converted.cue" : Path.GetFileName(Cdi2BinService.SuggestCuePath(Cdi2BinOutputBinBox.Text!)))
            : Path.GetFileName(Cdi2BinOutputCueBox.Text!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose CDI2BIN output CUE", SuggestedFileName = suggested,
            FileTypeChoices = new[] { new FilePickerFileType("CUE sheet") { Patterns = new[] { "*.cue" } } }
        });
        if (file?.TryGetLocalPath() is { } path) Cdi2BinOutputCueBox.Text = path;
    }

    private async void Cdi2BinAnalyzeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_cdi2BinCts is not null) return;
        try
        {
            _cdi2BinCts = new CancellationTokenSource(); SetCdi2BinRunning(true);
            Cdi2BinProgressBar.Value = 0; Cdi2BinProgressText.Text = "Analysing..."; Cdi2BinLogBox.Text = string.Empty;
            Cdi2BinInspection inspection = await AnalyzeCdi2BinFromUiAsync(_cdi2BinCts.Token);
            _cdi2BinInspection = inspection; ApplyCdi2BinInspection(inspection);
            AppendCdi2BinLog($"DiscJuggler CDI v{inspection.FormatVersion}; {inspection.SessionCount} session(s); {inspection.Tracks.Count} track(s).");
            foreach (string warning in inspection.Warnings) AppendCdi2BinLog($"WARNING: {warning}");
            Cdi2BinProgressText.Text = "Analysed";
        }
        catch (OperationCanceledException) { Cdi2BinProgressText.Text = "Cancelled"; }
        catch (Exception ex) { _cdi2BinInspection = null; Cdi2BinProgressText.Text = "Error"; AppendCdi2BinLog($"ERROR: {ex.Message}"); await ShowMessageAsync("DumpToolbox — CDI2BIN", ex.Message); }
        finally { _cdi2BinCts?.Dispose(); _cdi2BinCts = null; SetCdi2BinRunning(false); }
    }

    private async void Cdi2BinConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_cdi2BinCts is not null) return;
        try
        {
            string input = Cdi2BinInputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("Choose a DiscJuggler CDI image.");

            _cdi2BinCts = new CancellationTokenSource(); SetCdi2BinRunning(true); Cdi2BinLogBox.Text = string.Empty; Cdi2BinProgressBar.Value = 0; Cdi2BinProgressText.Text = "Analysing...";
            Cdi2BinInspection inspection = await _cdi2BinService.AnalyzeAsync(input, _cdi2BinCts.Token);
            _cdi2BinInspection = inspection; ApplyCdi2BinInspection(inspection);
            string outputBin = Cdi2BinOutputBinBox.Text?.Trim() ?? string.Empty;
            string outputCue = Cdi2BinOutputCueBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outputBin)) outputBin = inspection.IsDvd ? Cdi2BinService.SuggestIsoPath(input) : Cdi2BinService.SuggestBinPath(input);
            if (inspection.IsDvd) { outputBin = Path.ChangeExtension(outputBin, ".iso"); outputCue = string.Empty; }
            else if (string.IsNullOrWhiteSpace(outputCue)) outputCue = Cdi2BinService.SuggestCuePath(outputBin);
            Cdi2BinOutputBinBox.Text = outputBin; Cdi2BinOutputCueBox.Text = outputCue;
            foreach (string warning in inspection.Warnings) AppendCdi2BinLog($"WARNING: {warning}");

            var sw = Stopwatch.StartNew();
            var progress = new Progress<Cdi2BinProgress>(p =>
            {
                Cdi2BinProgressBar.Value = p.Fraction * 100;
                double rate = p.InputBytesProcessed / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                Cdi2BinProgressText.Text = $"{p.Fraction:P1}  {rate:N1} MiB/s"; SetWindowStatus($"CDI2BIN — {p.Fraction:P0}");
            });
            Cdi2BinResult result = await _cdi2BinService.ConvertAsync(input, outputBin, outputCue, Cdi2BinSaveSubCheckBox.IsChecked == true, progress, new Progress<string>(AppendCdi2BinLog), _cdi2BinCts.Token);
            sw.Stop(); Cdi2BinProgressBar.Value = 100; Cdi2BinProgressText.Text = $"Complete — {result.SectorCount:N0} sectors";
            AppendCdi2BinLog($"Complete in {sw.Elapsed}."); AppendCdi2BinLog($"{(inspection.IsDvd ? "ISO" : "BIN")}: {result.OutputBinPath} ({result.OutputBytes:N0} bytes)"); if (!inspection.IsDvd) AppendCdi2BinLog($"CUE: {result.OutputCuePath}");
            if (result.OutputSubPath is not null) AppendCdi2BinLog($"SUB: {result.OutputSubPath}");
            if (!inspection.IsDvd && Cdi2BinSendToFindCrcsCheckBox.IsChecked == true) { AppendCdi2BinLog($"FindCRCs source set to: {result.OutputBinPath}"); SendBinToFindCrcs(result.OutputBinPath); }
            SetWindowStatus();
        }
        catch (OperationCanceledException) { Cdi2BinProgressText.Text = "Cancelled"; AppendCdi2BinLog("Conversion cancelled. Partial output removed."); SetWindowStatus(); }
        catch (Exception ex) { Cdi2BinProgressText.Text = "Error"; AppendCdi2BinLog($"ERROR: {ex.Message}"); SetWindowStatus(); await ShowMessageAsync("DumpToolbox — CDI2BIN", ex.Message); }
        finally { _cdi2BinCts?.Dispose(); _cdi2BinCts = null; SetCdi2BinRunning(false); }
    }

    private void Cdi2BinCancelButton_Click(object? sender, RoutedEventArgs e) => _cdi2BinCts?.Cancel();

    private async Task<Cdi2BinInspection> AnalyzeCdi2BinFromUiAsync(CancellationToken token)
    {
        string input = Cdi2BinInputBox.Text?.Trim() ?? string.Empty;
        Cdi2BinInspection inspection = await _cdi2BinService.AnalyzeAsync(input, token);
        if (inspection.IsDvd) { Cdi2BinOutputBinBox.Text = Cdi2BinService.SuggestIsoPath(input); Cdi2BinOutputCueBox.Text = string.Empty; }
        else { Cdi2BinOutputBinBox.Text = Cdi2BinService.SuggestBinPath(input); Cdi2BinOutputCueBox.Text = Cdi2BinService.SuggestCuePath(Cdi2BinOutputBinBox.Text!); }
        return inspection;
    }

    private void ApplyCdi2BinInspection(Cdi2BinInspection inspection)
    {
        Cdi2BinOutputImageLabel.Text = inspection.IsDvd ? "Output ISO" : "Output BIN";
        Cdi2BinOutputCueLabel.IsVisible = !inspection.IsDvd; Cdi2BinOutputCueBox.IsVisible = !inspection.IsDvd; Cdi2BinOutputCueBrowseButton.IsVisible = !inspection.IsDvd;
        Cdi2BinSaveSubCheckBox.IsVisible = !inspection.IsDvd; Cdi2BinSendToFindCrcsCheckBox.IsVisible = !inspection.IsDvd;
        Cdi2BinOutputBinBox.Watermark = inspection.IsDvd ? "2048-byte DVD ISO output..." : "Raw 2352-byte BIN output...";
        var text = new StringBuilder();
        text.AppendLine($"DiscJuggler CDI v{inspection.FormatVersion}   Sessions: {inspection.SessionCount}   Tracks: {inspection.Tracks.Count}   Descriptor: 0x{inspection.DescriptorOffset:X}");
        text.AppendLine($"Media: {(inspection.IsDvd ? "DVD" : "CD")}   Output: {(inspection.IsDvd ? $"ISO: {inspection.OutputSectors:N0} sectors × 2048" : $"BIN/CUE: {inspection.OutputSectors:N0} sectors × 2352")} = {inspection.OutputBytes:N0} bytes");
        string sub = inspection.HasFullSubchannel ? (Cdi2BinSaveSubCheckBox.IsChecked == true ? "96-byte P-W stored → .sub output enabled" : "96-byte P-W stored → .sub output available (disabled)") : inspection.HasPqSubchannel ? "16-byte PQ-only subcode stored" : "none stored";
        if (!inspection.IsDvd) text.AppendLine($"Subchannel: {sub}"); text.AppendLine();
        text.AppendLine("Ses Trk Type                         Stored  P-W  PQ   Pregap      Data    Disc LBA   CDI offset          BIN INDEX01");
        text.AppendLine("--- --- ---------------------------- ------- ---- ---- --------- --------- ---------- ------------------- -----------");
        foreach (CdiTrackInspection t in inspection.Tracks)
            text.AppendLine($"{t.SessionNumber,3:00} {t.Number,3:00} {DescribeCdiKind(t.Kind),-28} {t.StoredSectorSize,7} {(t.HasFullSubchannel ? "yes" : "no"),4} {(t.HasPqSubchannel ? "yes" : "no"),4} {t.PregapSectors,9:N0} {t.DataSectors,9:N0} {t.DiscIndex01Lba,10:N0} 0x{t.SourceOffset,16:X} {FormatCdiCueTime(t.OutputIndex01Sector),11}");
        if (inspection.Warnings.Count > 0) { text.AppendLine(); foreach (string warning in inspection.Warnings) text.AppendLine($"WARNING: {warning}"); }
        Cdi2BinInspectionText.Text = text.ToString().TrimEnd();
    }

    private static string DescribeCdiKind(CdiTrackKind kind) => kind switch
    {
        CdiTrackKind.Audio => "Audio", CdiTrackKind.Mode1Cooked => "Mode 1 cooked → raw", CdiTrackKind.Mode1Raw => "Mode 1 raw",
        CdiTrackKind.Mode2Cooked2048 => "Mode 2 cooked → raw", CdiTrackKind.Mode2Body2336 => "Mode 2 2336 → raw", CdiTrackKind.Mode2Raw => "Mode 2 raw", _ => kind.ToString()
    };

    private void ResetCdi2BinMediaUi()
    {
        Cdi2BinOutputImageLabel.Text = "Output image"; Cdi2BinOutputBinBox.Watermark = "BIN for CD / ISO for DVD...";
        Cdi2BinOutputCueLabel.IsVisible = true; Cdi2BinOutputCueBox.IsVisible = true; Cdi2BinOutputCueBrowseButton.IsVisible = true;
        Cdi2BinSaveSubCheckBox.IsVisible = true; Cdi2BinSendToFindCrcsCheckBox.IsVisible = true;
    }

    private void SetCdi2BinRunning(bool running)
    {
        Cdi2BinInputBrowseButton.IsEnabled = !running; Cdi2BinOutputBinBrowseButton.IsEnabled = !running; Cdi2BinOutputCueBrowseButton.IsEnabled = !running;
        Cdi2BinAnalyzeButton.IsEnabled = !running; Cdi2BinConvertButton.IsEnabled = !running; Cdi2BinCancelButton.IsEnabled = running;
        Cdi2BinInputBox.IsReadOnly = running; Cdi2BinOutputBinBox.IsReadOnly = running; Cdi2BinOutputCueBox.IsReadOnly = running;
        Cdi2BinSaveSubCheckBox.IsEnabled = !running; Cdi2BinSendToFindCrcsCheckBox.IsEnabled = !running;
    }
    private void AppendCdi2BinLog(string message) => AppendLog(Cdi2BinLogBox, message);
    private static string FormatCdiCueTime(long sectors) => $"{sectors / 4500:00}:{(sectors / 75) % 60:00}:{sectors % 75:00}";
}
