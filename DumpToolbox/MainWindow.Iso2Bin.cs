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
    private async void IsoInputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose ISO/BIN backing image",
            AllowMultiple = false
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        IsoInputBox.Text = path;
        SetSuggestedIsoOutput(path, IsoCueBox.Text?.Trim());
        await InspectIsoInputsForUiAsync();
    }

    private async void IsoCueBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose mixed-mode CUE sheet",
            AllowMultiple = false
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } cuePath)
            return;

        IsoCueBox.Text = cuePath;
        IsoModeBox.IsEnabled = false;
        IsoInputBox.Text = string.Empty;
        IsoInputBox.IsReadOnly = true;
        IsoInputBrowseButton.IsEnabled = false;
        IsoTargetBox.Text = string.Empty;
        IsoTargetBox.IsReadOnly = true;
        IsoTargetClearButton.IsEnabled = false;

        try
        {
            CueInspectionResult inspection = await _iso2BinService.InspectCueAsync(cuePath);
            SetSuggestedIsoOutput(cuePath, cuePath);
            IsoInspectionText.Text = inspection.DetectionMessage;
        }
        catch
        {
            // Keep the selected CUE visible; the inspection text below will show the useful error.
        }

        await InspectIsoInputsForUiAsync();
    }

    private async void IsoCueClearButton_Click(object? sender, RoutedEventArgs e)
    {
        IsoCueBox.Text = string.Empty;
        IsoModeBox.IsEnabled = true;
        IsoInputBrowseButton.IsEnabled = true;
        IsoInputBox.IsReadOnly = false;
        IsoTargetBox.IsReadOnly = false;
        IsoTargetClearButton.IsEnabled = true;
        await InspectIsoInputsForUiAsync();
    }

    private async void IsoXaMetadataBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose DIC EccEdc log or raw Redumper skeleton",
            AllowMultiple = false
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        IsoXaMetadataBox.Text = path;
        await InspectXaMetadataForUiAsync();
    }

    private async void IsoXaMetadataClearButton_Click(object? sender, RoutedEventArgs e)
    {
        IsoXaMetadataBox.Text = string.Empty;
        IsoXaMetadataStatus.Text = "No XA metadata selected.";
        await InspectIsoInputsForUiAsync();
    }

    private async void IsoTargetClearButton_Click(object? sender, RoutedEventArgs e)
    {
        IsoTargetBox.Text = string.Empty;
        await InspectIsoInputsForUiAsync();
    }


    private async Task<(HashTarget? Target, RedumpDiscImportResult? Import)> ResolveOptionalIsoTargetAsync(string text)
    {
        if (!RedumpDiscImportService.TryParseDiscId(text, out int redumpDiscId))
            return (ParseOptionalIsoTarget(text), null);

        SetWindowStatus($"ISO2BIN — retrieving Redump disc {redumpDiscId}");
        RedumpDiscImportResult import = await RedumpDiscImportService.ImportAsync(redumpDiscId);
        string selectedText = import.TargetText;

        // ISO2BIN needs the data-track target, not the complete multi-track set.
        // Prefer the first non-AUDIO CUE track and match its FILE name to the
        // imported Redump payload row. A normal data-only disc naturally falls
        // through to the single imported payload row.
        if (!string.IsNullOrWhiteSpace(import.CuePath))
        {
            CueSheetAnalysis cue = await _cueSheetAnalysisService.AnalyzeAsync(import.CuePath);
            CueTrackAnalysis? dataTrack = cue.Tracks.FirstOrDefault(t => !t.IsAudio);
            if (dataTrack is not null)
            {
                string dataName = Path.GetFileName(dataTrack.FileName);
                string? matchingRow = import.TargetText
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line =>
                    {
                        HashTarget? parsed = TargetParser.Parse(line).FirstOrDefault();
                        string? fileName = parsed?.OutputFileName ?? parsed?.Label;
                        return !string.IsNullOrWhiteSpace(fileName) &&
                               string.Equals(Path.GetFileName(fileName), dataName,
                                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    });
                if (!string.IsNullOrWhiteSpace(matchingRow))
                    selectedText = matchingRow;
            }
        }

        IReadOnlyList<HashTarget> targets = TargetParser.Parse(selectedText);
        if (targets.Count != 1)
            throw new InvalidOperationException($"Redump disc {redumpDiscId} exposes {targets.Count} payload targets and ISO2BIN could not uniquely identify the data track.");

        IsoTargetBox.Text = selectedText;
        HashTarget target = targets[0];
        if (target.Size % Iso2BinService.RawSectorSize != 0)
            throw new InvalidOperationException($"Redump target size {target.Size:N0} is not an exact multiple of 2352 bytes.");
        return (target, import);
    }
    private static HashTarget? ParseOptionalIsoTarget(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        IReadOnlyList<HashTarget> targets = TargetParser.Parse(text);
        if (targets.Count != 1)
            throw new InvalidOperationException("ISO2BIN accepts exactly one optional Redump target entry.");
        HashTarget target = targets[0];
        if (target.Size % Iso2BinService.RawSectorSize != 0)
            throw new InvalidOperationException($"Redump target size {target.Size:N0} is not an exact multiple of 2352 bytes.");
        return target;
    }

    private async Task InspectXaMetadataForUiAsync()
    {
        string metadata = IsoXaMetadataBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(metadata))
        {
            IsoXaMetadataStatus.Text = "No XA metadata selected.";
            return;
        }

        try
        {
            XaMetadataInspection inspection = await _iso2BinService.InspectXaMetadataAsync(metadata);
            IsoXaMetadataStatus.Text = inspection.DetectionMessage;
        }
        catch (Exception ex)
        {
            IsoXaMetadataStatus.Text = $"Unable to inspect XA metadata: {ex.Message}";
        }
    }

    private void SetSuggestedIsoOutput(string inputPath, string? cuePath)
    {
        if (!string.IsNullOrWhiteSpace(IsoOutputBox.Text))
            return;

        string directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        string inputStem = Path.GetFileNameWithoutExtension(inputPath);
        string extension = Path.GetExtension(inputPath);

        string stem = !string.IsNullOrWhiteSpace(cuePath)
            ? Path.GetFileNameWithoutExtension(cuePath)
            : inputStem;

        string fileName = !string.IsNullOrWhiteSpace(cuePath) || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            ? stem + "_2352.bin"
            : stem + ".bin";

        if (string.IsNullOrWhiteSpace(cuePath))
        {
            try
            {
                HashTarget? target = ParseOptionalIsoTarget(IsoTargetBox.Text ?? string.Empty);
                string? targetOutputFileName = target?.OutputFileName;
                if (!string.IsNullOrWhiteSpace(targetOutputFileName))
                    fileName = Path.GetFileName(targetOutputFileName);
            }
            catch
            {
                // Keep the ordinary suggestion; conversion/inspection will show target parse errors.
            }
        }

        IsoOutputBox.Text = Path.Combine(directory, fileName);
    }

    private async void IsoOutputBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        string input = IsoInputBox.Text?.Trim() ?? string.Empty;
        string cue = IsoCueBox.Text?.Trim() ?? string.Empty;
        string stem = !string.IsNullOrWhiteSpace(cue)
            ? Path.GetFileNameWithoutExtension(cue)
            : string.IsNullOrWhiteSpace(input)
                ? "converted"
                : Path.GetFileNameWithoutExtension(input);

        string suggestedName = !string.IsNullOrWhiteSpace(cue) ? stem + "_2352.bin" : stem + ".bin";
        if (string.IsNullOrWhiteSpace(cue))
        {
            try
            {
                HashTarget? target = ParseOptionalIsoTarget(IsoTargetBox.Text ?? string.Empty);
                string? targetOutputFileName = target?.OutputFileName;
                if (!string.IsNullOrWhiteSpace(targetOutputFileName))
                    suggestedName = Path.GetFileName(targetOutputFileName);
            }
            catch
            {
                // Target validation is handled by the main conversion path.
            }
        }
        if (!string.IsNullOrWhiteSpace(input) &&
            Path.GetExtension(input).Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(input), suggestedName, StringComparison.OrdinalIgnoreCase))
        {
            suggestedName = stem + "_2352.bin";
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose 2352-byte/sector BIN output",
            SuggestedFileName = suggestedName
        });

        if (file?.TryGetLocalPath() is { } path)
            IsoOutputBox.Text = path;
    }

    private async Task InspectIsoInputsForUiAsync()
    {
        string input = IsoInputBox.Text?.Trim() ?? string.Empty;
        string cue = IsoCueBox.Text?.Trim() ?? string.Empty;

        await InspectXaMetadataForUiAsync();

        try
        {
            if (!string.IsNullOrWhiteSpace(cue))
            {
                CueInspectionResult inspection = await _iso2BinService.InspectCueAsync(cue);

                IsoInspectionText.Text = inspection.DetectionMessage;
                return;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                IsoInspectionText.Text = "No input selected.";
                return;
            }

            IsoInspectionResult isoInspection = await _iso2BinService.InspectAsync(input);
            string inspectionText = isoInspection.Is2048Aligned
                ? $"{isoInspection.InputBytes:N0} bytes = {isoInspection.SectorCount:N0} sectors. {isoInspection.DetectionMessage}"
                : isoInspection.DetectionMessage;
            HashTarget? target = ParseOptionalIsoTarget(IsoTargetBox.Text ?? string.Empty);
            if (target is not null && isoInspection.Is2048Aligned)
            {
                long targetSectors = target.Size / Iso2BinService.RawSectorSize;
                long delta = targetSectors - isoInspection.SectorCount;
                inspectionText += delta switch
                {
                    > 0 => $" Redump target requires {targetSectors:N0} sectors: append {delta:N0} empty 2048-byte sector(s).",
                    < 0 => $" Redump target requires {targetSectors:N0} sectors: remove {-delta:N0} trailing 2048-byte sector(s).",
                    _ => $" Redump target length matches ({targetSectors:N0} sectors)."
                };
            }
            IsoInspectionText.Text = inspectionText;
        }
        catch (Exception ex)
        {
            IsoInspectionText.Text = $"Unable to inspect input: {ex.Message}";
        }
    }

    private static bool IsOrdinaryIsoOutputSuggestion(string inputPath, string outputPath)
    {
        string fullInput = Path.GetFullPath(inputPath);
        string directory = Path.GetDirectoryName(fullInput) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(fullInput);
        string extension = Path.GetExtension(fullInput);
        string ordinaryName = extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            ? stem + "_2352.bin"
            : stem + ".bin";
        string ordinaryPath = Path.Combine(directory, ordinaryName);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(ordinaryPath), comparison);
    }

    private async void IsoConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_iso2BinCts is not null)
            return;

        try
        {
            string input = IsoInputBox.Text?.Trim() ?? string.Empty;
            string cue = IsoCueBox.Text?.Trim() ?? string.Empty;
            string xaMetadata = IsoXaMetadataBox.Text?.Trim() ?? string.Empty;
            var isoTargetResolved = await ResolveOptionalIsoTargetAsync(IsoTargetBox.Text ?? string.Empty);
            HashTarget? redumpTarget = isoTargetResolved.Target;
            RedumpDiscImportResult? redumpImport = isoTargetResolved.Import;
            string output = IsoOutputBox.Text?.Trim() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(cue) && redumpTarget is not null)
                throw new InvalidOperationException("The optional Redump target length adjustment is for ISO-only conversion and cannot be combined with a CUE.");

            string? redumpOutputFileName = redumpTarget?.OutputFileName;
            if (!string.IsNullOrWhiteSpace(input) && !string.IsNullOrWhiteSpace(redumpOutputFileName))
            {
                string targetFileName = Path.GetFileName(redumpOutputFileName);
                string inputDirectory = Path.GetDirectoryName(Path.GetFullPath(input)) ?? Directory.GetCurrentDirectory();
                string targetOutput = Path.Combine(inputDirectory, targetFileName);
                if (string.IsNullOrWhiteSpace(output) || IsOrdinaryIsoOutputSuggestion(input, output))
                {
                    output = targetOutput;
                    IsoOutputBox.Text = output;
                }
            }

            if (string.IsNullOrWhiteSpace(cue) && string.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("Choose an input ISO/BIN image or a CUE sheet.");
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("Choose an output BIN filename.");

            IsoLogBox.Text = string.Empty;
            if (redumpImport is not null)
                AppendIsoLog($"REDUMP: imported disc {redumpImport.DiscId} — {redumpImport.DiscTitle}; selected data target {TargetName(redumpTarget!)}.");
            AppendIsoLog($"Output: {output}");
            if (!string.IsNullOrWhiteSpace(xaMetadata))
            {
                XaMetadataInspection metadataInspection = await _iso2BinService.InspectXaMetadataAsync(xaMetadata);
                IsoXaMetadataStatus.Text = metadataInspection.DetectionMessage;
                AppendIsoLog($"XA metadata: {xaMetadata}");
                AppendIsoLog(metadataInspection.DetectionMessage);
            }

            bool cueMode = !string.IsNullOrWhiteSpace(cue);

            if (cueMode)
            {
                CueInspectionResult inspection = await _iso2BinService.InspectCueAsync(cue);

                IsoInspectionText.Text = inspection.DetectionMessage;

                AppendIsoLog($"CUE: {cue}");
                AppendIsoLog(inspection.DetectionMessage);
                foreach (string sourceFile in inspection.SourceFiles)
                    AppendIsoLog($"Source file: {sourceFile}");
                foreach (CueTrackInspection track in inspection.Tracks)
                {
                    AppendIsoLog(
                        $"Track {track.Number:00}: {Path.GetFileName(track.SourceFilePath)} [{track.SourceFileType}] " +
                        $"{track.SourceType} -> {track.OutputType}, {track.SectorCount:N0} sectors, " +
                        $"source offset {track.SourceOffset:N0}.");
                }

            }
            else
            {
                IsoInspectionResult inspection = await _iso2BinService.InspectAsync(input);
                IsoInspectionText.Text = inspection.Is2048Aligned
                    ? $"{inspection.InputBytes:N0} bytes = {inspection.SectorCount:N0} sectors. {inspection.DetectionMessage}"
                    : inspection.DetectionMessage;

                if (!inspection.Is2048Aligned)
                    throw new InvalidOperationException(inspection.DetectionMessage);

                AppendIsoLog($"Input: {input}");
                AppendIsoLog($"Input validated: {inspection.SectorCount:N0} sectors × 2048 bytes.");
                if (redumpTarget is not null)
                    AppendIsoLog($"Target: {TargetName(redumpTarget)} — {redumpTarget.Size:N0} bytes, CRC32={redumpTarget.Crc32Hex}, MD5={redumpTarget.NormalizedMd5 ?? "(none)"}" +
                                 (redumpTarget.NormalizedSha1 is null ? string.Empty : $", SHA-1={redumpTarget.NormalizedSha1}"));
            }

            _iso2BinCts = new CancellationTokenSource();
            SetIso2BinRunning(true);

            var stopwatch = Stopwatch.StartNew();
            long lastLoggedSectors = -1;
            var progress = new Progress<Iso2BinProgress>(p =>
            {
                IsoProgressBar.Value = p.Fraction * 100;
                double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double mibPerSecond = p.InputBytesProcessed / 1048576.0 / seconds;
                IsoProgressText.Text = $"{p.Fraction:P1}  {mibPerSecond:N1} MiB/s";
                SetWindowStatus($"ISO2BIN — {p.Fraction:P0}");

                long logInterval = Math.Max(1, p.TotalSectors / 4);
                if (lastLoggedSectors < 0 || p.SectorsProcessed - lastLoggedSectors >= logInterval || p.SectorsProcessed == p.TotalSectors)
                {
                    AppendIsoLog($"Progress: {p.Fraction:P1} | {p.SectorsProcessed:N0} / {p.TotalSectors:N0} sectors | {mibPerSecond:N1} MiB/s input");
                    lastLoggedSectors = p.SectorsProcessed;
                }
            });

            var activity = new Progress<string>(AppendIsoLog);
            Iso2BinResult result;

            if (cueMode)
            {
                result = await _iso2BinService.ConvertCueAsync(
                    cue,
                    output,
                    string.IsNullOrWhiteSpace(xaMetadata) ? null : xaMetadata,
                    progress,
                    activity,
                    _iso2BinCts.Token);
            }
            else
            {
                Iso2BinModeSelection selection = IsoModeBox.SelectedIndex switch
                {
                    1 => Iso2BinModeSelection.Mode1,
                    2 => Iso2BinModeSelection.Mode2Form1,
                    _ => Iso2BinModeSelection.Auto
                };

                result = await _iso2BinService.ConvertAsync(
                    input,
                    output,
                    selection,
                    string.IsNullOrWhiteSpace(xaMetadata) ? null : xaMetadata,
                    redumpTarget,
                    progress,
                    activity,
                    _iso2BinCts.Token);
            }

            stopwatch.Stop();
            IsoProgressBar.Value = 100;
            IsoProgressText.Text = $"Complete — {result.SectorCount:N0} sectors";
            AppendIsoLog($"Complete in {stopwatch.Elapsed}.");

            if (!cueMode)
                AppendIsoLog($"Mode: {Iso2BinService.FormatMode(result.Mode)}.");

            AppendIsoLog($"Wrote {result.OutputBytes:N0} bytes ({result.SectorCount:N0} × 2352) to: {result.OutputPath}");
            if (!cueMode && redumpTarget is not null)
            {
                string? expectedMd5 = redumpTarget.NormalizedMd5;
                string? expectedSha1 = redumpTarget.NormalizedSha1;
                var verifyOptions = new HashCalculationOptions(Crc32: true, Md5: expectedMd5 is not null, Sha1: expectedSha1 is not null);
                HashCalculationResult verify = await _hashCalculationService.CalculateAsync(result.OutputPath, verifyOptions, cancellationToken: _iso2BinCts.Token);
                string actualCrc = verify.Hashes["CRC32"];
                string? actualMd5 = verify.Hashes.TryGetValue("MD5", out string? md5) ? md5 : null;
                string? actualSha1 = verify.Hashes.TryGetValue("SHA-1", out string? sha1) ? sha1 : null;
                bool sizeOk = verify.FileLength == redumpTarget.Size;
                bool crcOk = actualCrc.Equals(redumpTarget.Crc32Hex, StringComparison.OrdinalIgnoreCase);
                bool md5Ok = expectedMd5 is null || expectedMd5.Equals(actualMd5, StringComparison.OrdinalIgnoreCase);
                bool sha1Ok = expectedSha1 is null || expectedSha1.Equals(actualSha1, StringComparison.OrdinalIgnoreCase);
                AppendIsoLog($"Target verification: size {(sizeOk ? "MATCH" : "FAIL")}; CRC32 {(crcOk ? "MATCH" : $"FAIL ({actualCrc})")}; MD5 {(md5Ok ? "MATCH" : $"FAIL ({actualMd5})")}" +
                             (redumpTarget.NormalizedSha1 is null ? string.Empty : $"; SHA-1 {(sha1Ok ? "MATCH" : $"FAIL ({actualSha1})")}"));
                if (sizeOk && crcOk && md5Ok && sha1Ok)
                    AppendIsoLog("*** REDUMP TARGET MATCHED ***");
            }
            if (!string.IsNullOrWhiteSpace(result.OutputCuePath))
                AppendIsoLog($"Generated replacement CUE: {result.OutputCuePath}");

            if (IsoSendToFindCrcsCheckBox.IsChecked == true)
            {
                AppendIsoLog($"FindCRCs source set to: {result.OutputPath}");
                SendBinToFindCrcs(result.OutputPath);
            }

            SetWindowStatus();
        }
        catch (OperationCanceledException)
        {
            AppendIsoLog("Conversion cancelled. Partial output removed.");
            IsoProgressText.Text = "Cancelled";
            SetWindowStatus();
        }
        catch (Exception ex)
        {
            AppendIsoLog($"ERROR: {ex.Message}");
            IsoProgressText.Text = "Error";
            SetWindowStatus();
            await ShowMessageAsync("DumpToolbox — ISO2BIN", ex.Message);
        }
        finally
        {
            _iso2BinCts?.Dispose();
            _iso2BinCts = null;
            SetIso2BinRunning(false);
        }
    }

    private void IsoCancelButton_Click(object? sender, RoutedEventArgs e) => _iso2BinCts?.Cancel();

    private void SetIso2BinRunning(bool running)
    {
        IsoConvertButton.IsEnabled = !running;
        IsoCancelButton.IsEnabled = running;
        bool cueMode = !string.IsNullOrWhiteSpace(IsoCueBox.Text);
        IsoInputBrowseButton.IsEnabled = !running && !cueMode;
        IsoCueBrowseButton.IsEnabled = !running;
        IsoCueClearButton.IsEnabled = !running;
        IsoXaMetadataBrowseButton.IsEnabled = !running;
        IsoXaMetadataClearButton.IsEnabled = !running;
        IsoOutputBrowseButton.IsEnabled = !running;
        IsoInputBox.IsReadOnly = running || cueMode;
        IsoCueBox.IsReadOnly = running;
        IsoXaMetadataBox.IsReadOnly = running;
        IsoTargetBox.IsReadOnly = running || cueMode;
        IsoTargetClearButton.IsEnabled = !running && !cueMode;
        IsoOutputBox.IsReadOnly = running;
        IsoModeBox.IsEnabled = !running && string.IsNullOrWhiteSpace(IsoCueBox.Text);
        IsoSendToFindCrcsCheckBox.IsEnabled = !running;
    }

    private void SendBinToFindCrcs(string binPath)
    {
        FilePathBox.Text = binPath;
        MainTabControl.SelectedIndex = 0;
    }

    private void AppendIsoLog(string message) => AppendLog(IsoLogBox, message);

    // Skeletool tab
}
