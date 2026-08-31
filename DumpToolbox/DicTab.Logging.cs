using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private void AppendDicVerboseInspectionAudit(SkeletonInspectionResult inspection)
    {
        AppendDicLog("VERBOSE DIC: ===== imported filesystem record audit =====");
        AppendDicLog($"VERBOSE DIC: {inspection.Entries.Count:N0} imported recovery entry/entries; HashEntryCount={inspection.HashEntryCount:N0}; UnmappedHashEntryCount={inspection.UnmappedHashEntryCount:N0}.");

        var duplicateGroups = inspection.Entries
            .Where(e => e.SpecialKind == SkeletonSpecialKind.None)
            .GroupBy(e => NormalizeVerboseIsoPath(e.IsoOriginalPath ?? e.Path), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || g.Any(e => e.AlternateIsoRecords is { Count: > 0 }))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AppendDicLog($"VERBOSE DIC: duplicate/alternate pathname groups: {duplicateGroups.Length:N0}.");
        if (inspection.DicSupplementaryDirectoryHints is { Count: > 0 } supplementaryHints)
        {
            AppendDicLog($"VERBOSE DIC: supplementary/Joliet path-table evidence: {supplementaryHints.Count:N0} directory record(s) recovered from volDesc.");
            foreach (DicSupplementaryDirectoryHint hint in supplementaryHints.OrderBy(h => h.DirectoryNumber))
            {
                AppendDicLog($"VERBOSE DIC JOLIET PATH {hint.DirectoryNumber:N0}: path='{hint.Path}' extentLBA={hint.ExtentLba:N0} parentDirectoryNumber={hint.ParentDirectoryNumber:N0}.");
            }
        }
        else
        {
            AppendDicLog("VERBOSE DIC: supplementary/Joliet path-table evidence: none recovered from volDesc; child-directory placement will require another proven allocator or the conservative SVD-root fallback.");
        }


        foreach (var group in duplicateGroups)
        {
            AppendDicLog($"VERBOSE DIC DUPLICATE: '{group.Key}' has {group.Count():N0} imported logical entry/entries.");
            foreach (SkeletonContentEntry entry in group.OrderBy(e => e.IsoRecordExtentLba ?? e.ExtentLba).ThenBy(e => e.DataLength))
                AppendDicLog("VERBOSE DIC DUPLICATE RECORD: " + FormatVerboseEntry(entry));
        }

        int ordinal = 0;
        foreach (SkeletonContentEntry entry in inspection.Entries
                     .OrderBy(e => e.SpecialKind)
                     .ThenBy(e => e.IsoOriginalPath ?? e.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.IsoRecordExtentLba ?? e.ExtentLba)
                     .ThenBy(e => e.DataLength))
        {
            ordinal++;
            AppendDicLog($"VERBOSE DIC RECORD {ordinal:N0}: {FormatVerboseEntry(entry)}");
        }
        AppendDicLog("VERBOSE DIC: ===== end imported filesystem record audit =====");
    }

    private void AppendDicVerboseManifestAudit(
        SkeletonInspectionResult inspection,
        string sourceFolder,
        IsoExtractionManifest manifest,
        bool payloadOnly)
    {
        AppendDicLog("VERBOSE DIC: ===== ISO Extractor manifest audit =====");
        AppendDicLog($"VERBOSE DIC: extractor manifest contains {manifest.Files.Count:N0} ISO filesystem record(s). This count does not include SkeleTool .hash pseudo-records such as SYSTEM_AREA.");
        AppendDicLog(payloadOnly
            ? "VERBOSE DIC: manifest trust mode = PAYLOAD ONLY. Extractor LBA/extent geometry is diagnostic only and will not influence DIC placement."
            : "VERBOSE DIC: manifest trust mode = FULL PVD IDENTITY. Exact extractor record geometry may be used.");

        var dicEntries = inspection.Entries
            .Where(e => e.SpecialKind == SkeletonSpecialKind.None)
            .ToArray();
        int exact = 0, compatible = 0, missing = 0;
        int ordinal = 0;

        foreach (IsoExtractionManifestFile record in manifest.Files
                     .OrderBy(r => r.IsoPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.ExtentLba)
                     .ThenBy(r => r.DataLength))
        {
            ordinal++;
            string isoPath = NormalizeVerboseIsoPath(record.IsoPath);
            SkeletonContentEntry[] samePath = dicEntries
                .Where(e => NormalizeVerboseIsoPath(e.IsoOriginalPath ?? e.Path).Equals(isoPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            SkeletonContentEntry[] exactRecords = samePath
                .Where(e => (e.IsoRecordExtentLba ?? e.ExtentLba) == record.ExtentLba)
                .Where(e => e.DataLength == record.DataLength)
                .Where(e => e.IsoFileFlags == record.FileFlags)
                .ToArray();
            SkeletonContentEntry[] compatibleRecords = samePath
                .Where(e => e.DataLength == record.DataLength)
                .Where(e => e.IsoFileFlags == record.FileFlags)
                .ToArray();

            string resolution;
            if (!payloadOnly && exactRecords.Length == 1)
            {
                exact++;
                resolution = "EXACT DIC RECORD";
            }
            else if (compatibleRecords.Length == 1)
            {
                compatible++;
                resolution = payloadOnly
                    ? $"PAYLOAD-ONLY UNIQUE PATH+SIZE+FLAGS DIC RECORD (authoritative DIC LBA {(compatibleRecords[0].IsoRecordExtentLba ?? compatibleRecords[0].ExtentLba):N0}; extractor LBA ignored)"
                    : $"UNIQUE PATH+SIZE+FLAGS DIC RECORD (DIC LBA {(compatibleRecords[0].IsoRecordExtentLba ?? compatibleRecords[0].ExtentLba):N0})";
            }
            else
            {
                missing++;
                resolution = samePath.Length == 0
                    ? "NO DIC PATH RECORD"
                    : $"NO UNIQUE DIC GEOMETRY ({samePath.Length:N0} same-path DIC candidate(s))";
            }

            string extractedPath = Path.GetFullPath(Path.Combine(sourceFolder, record.ExtractedRelativePath));
            string fileState = File.Exists(extractedPath)
                ? $"present actualLen={new FileInfo(extractedPath).Length:N0}"
                : "MISSING ON DISK";
            string extents = record.Extents.Count == 0
                ? "none"
                : string.Join(",", record.Extents.Select(x => $"{x.ExtentLba}+{x.DataLength}"));

            AppendDicLog($"VERBOSE DIC MANIFEST {ordinal:N0}: iso='{record.IsoPath}' LBA={record.ExtentLba:N0} len={record.DataLength:N0} flags=0x{record.FileFlags:X2} assoc={record.IsAssociated} extents=[{extents}] extracted='{record.ExtractedRelativePath}' {fileState} => {resolution}.");
        }

        AppendDicLog($"VERBOSE DIC: manifest reconciliation summary: exact={exact:N0}; unique path+size+flags fallback={compatible:N0}; unresolved/not represented={missing:N0}.");
        AppendDicLog("VERBOSE DIC: ===== end ISO Extractor manifest audit =====");
    }

    private void AppendDicVerboseMatchAudit(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> found)
    {
        AppendDicLog("VERBOSE DIC: ===== final source-match audit =====");
        int ordinal = 0;
        foreach (SkeletonSourceMatch match in found.Values
                     .OrderBy(m => m.Entry.IsoOriginalPath ?? m.Entry.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(m => m.Entry.IsoRecordExtentLba ?? m.Entry.ExtentLba)
                     .ThenBy(m => m.Entry.DataLength))
        {
            ordinal++;
            long actualLength = File.Exists(match.SourcePath) ? new FileInfo(match.SourcePath).Length : -1;
            AppendDicLog(
                $"VERBOSE DIC MATCH {ordinal:N0}: {FormatVerboseEntry(match.Entry)} <= source='{match.SourcePath}' sourceRel='{match.SourceRelativePath ?? "n/a"}' sourceLen={(match.SourceLength ?? actualLength):N0} actualFileLen={actualLength:N0} sourceImageLba={(match.SourceImageLba?.ToString("N0") ?? "n/a")} method='{match.MatchMethod}'.");
        }

        SkeletonContentEntry[] unmatched = inspection.Entries
            .Where(IsRequiredDicSourceEntry)
            .Where(entry => !found.ContainsKey(entry.Path) && !_dicMatches.ContainsKey(entry.Path) && !_dicAppliedEntries.Contains(entry.Path))
            .ToArray();
        AppendDicLog($"VERBOSE DIC: unmatched required entries after this scan: {unmatched.Length:N0}.");
        foreach (SkeletonContentEntry entry in unmatched
                     .OrderBy(e => e.IsoOriginalPath ?? e.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.IsoRecordExtentLba ?? e.ExtentLba))
            AppendDicLog("VERBOSE DIC UNMATCHED: " + FormatVerboseEntry(entry));
        AppendDicLog("VERBOSE DIC: ===== end final source-match audit =====");
    }

    private static string FormatVerboseEntry(SkeletonContentEntry entry)
    {
        string extents = entry.Extents is { Count: > 0 }
            ? string.Join(",", entry.Extents.Select(x => $"{x.ExtentLba}+{x.DataLength}"))
            : "none";
        string alternates = entry.AlternateIsoRecords is { Count: > 0 }
            ? string.Join(",", entry.AlternateIsoRecords.Select(x => $"{x.ExtentLba}+{x.DataLength}"))
            : "none";
        return $"path='{entry.Path}' iso='{entry.IsoOriginalPath ?? entry.Path}' LBA={entry.ExtentLba:N0} recordLBA={(entry.IsoRecordExtentLba?.ToString("N0") ?? "n/a")} len={entry.DataLength:N0} flags=0x{entry.IsoFileFlags:X2} special={entry.SpecialKind} requiresSource={entry.RequiresSource} extents=[{extents}] alternates=[{alternates}]";
    }

    private static string NormalizeVerboseIsoPath(string path) =>
        "/" + (path ?? string.Empty).Replace('\\', '/').Trim('/');

    private void AppendDicLog(string message)
    {
        _dicLogQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        SignalDicLogPump();
    }

    private void SignalDicLogPump()
    {
        if (Interlocked.Exchange(ref _dicLogSignalPending, 1) == 0)
            _dicLogSignal.Release();
    }

    private async Task DicLogPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _dicLogSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                var batch = new List<string>(1024);
                while (batch.Count < 5000 && _dicLogQueue.TryDequeue(out string? line))
                    batch.Add(line);

                if (batch.Count == 0)
                    continue;

                await Dispatcher.UIThread.InvokeAsync(
                    () => AppendDicLogLines(batch),
                    DispatcherPriority.Background);

                Interlocked.Exchange(ref _dicLogSignalPending, 0);
                if (!_dicLogQueue.IsEmpty)
                    SignalDicLogPump();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AppendDicLogLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        foreach (string line in lines)
        {
            if (_dicLogText.Length > 0)
                _dicLogText.AppendLine();
            _dicLogText.Append(line);
        }

        DicLogBox.Text = _dicLogText.ToString();
        DicLogBox.CaretIndex = DicLogBox.Text.Length;
        KeepLogAtLeftEdge(DicLogBox);
        UpdateDicDetachedLog();
    }

    private void ClearDicLog()
    {
        while (_dicLogQueue.TryDequeue(out _))
        {
        }
        while (_dicLogSignal.Wait(0))
        {
        }
        Interlocked.Exchange(ref _dicLogSignalPending, 0);
        _dicLogText.Clear();
        DicLogBox.Text = string.Empty;
        UpdateDicDetachedLog();
    }

}
