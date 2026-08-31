using System.Text.Json;

namespace DumpToolbox.Core;

public sealed class DicRecoveryState
{
    public int Version { get; set; } = 31;
    public string BaseName { get; set; } = string.Empty;
    public string VolumeIdentifier { get; set; } = string.Empty;
    public long SectorCount { get; set; }
    public string? ExpectedImageSha1 { get; set; }
    public string? LastDonorImagePath { get; set; }
    public string? LastOutputPath { get; set; }
    public bool DonorRequirementsSatisfied { get; set; }
    public List<string> AppliedEntries { get; set; } = new();
    public List<DicRecoveryStateMatch> Matches { get; set; } = new();
}

public sealed class DicRecoveryStateMatch
{
    public string EntryPath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string MatchMethod { get; set; } = string.Empty;
    public string? SourceRelativePath { get; set; }
    public bool Applied { get; set; }
}

public sealed record DicRecoveryStateLoadResult(
    DicRecoveryState State,
    IReadOnlyDictionary<string, SkeletonSourceMatch> Matches,
    int StaleMatches);

public sealed class DicRecoveryStateService
{
    private const int CurrentVersion = 41;

    public string GetStatePath(DicLogSet logs)
        => Path.Combine(logs.Directory, logs.BaseName + ".dumptoolbox_dicstate.json");

    public async Task<DicRecoveryStateLoadResult> LoadAsync(
        string statePath,
        DicLogSet logs,
        SkeletonInspectionResult inspection,
        CancellationToken cancellationToken = default)
    {
        DicRecoveryState state = new()
        {
            Version = CurrentVersion,
            BaseName = logs.BaseName,
            VolumeIdentifier = inspection.VolumeIdentifier,
            SectorCount = inspection.SectorCount,
            ExpectedImageSha1 = inspection.ExpectedImageSha1
        };

        if (File.Exists(statePath))
        {
            try
            {
                await using FileStream stream = new(
                    statePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                DicRecoveryState? loaded = await JsonSerializer.DeserializeAsync<DicRecoveryState>(
                    stream,
                    cancellationToken: cancellationToken);

                if (loaded is not null && IsSameRecovery(loaded, logs, inspection))
                    state = loaded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A damaged state file must never prevent a recovery. Start fresh.
            }
        }

        Dictionary<string, SkeletonContentEntry> entries = inspection.Entries
            .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
        int stale = 0;
        var validStateMatches = new List<DicRecoveryStateMatch>();

        HashSet<string> appliedEntryPaths = (state.AppliedEntries ?? new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (DicRecoveryStateMatch saved in state.Matches ?? new List<DicRecoveryStateMatch>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(saved.EntryPath, out SkeletonContentEntry? entry) ||
                string.IsNullOrWhiteSpace(saved.SourcePath))
            {
                stale++;
                continue;
            }

            bool alreadyApplied = saved.Applied || appliedEntryPaths.Contains(saved.EntryPath);
            bool sourceStillUsable = File.Exists(saved.SourcePath);
            if (sourceStillUsable)
            {
                try
                {
                    var info = new FileInfo(saved.SourcePath);
                    sourceStillUsable = info.Length == entry.DataLength &&
                        (saved.Length <= 0 || info.Length == saved.Length);
                }
                catch
                {
                    sourceStillUsable = false;
                }
            }

            // Once a payload has been committed to the cumulative working BIN, its
            // source bytes are no longer required.  Its source-relative pathname and
            // proven match method remain essential evidence for reconstructing the
            // supplementary/Joliet namespace, however.  Retain that metadata-only
            // match even if the original extracted source file has since been moved
            // or deleted.  ResurrectAsync filters applied entries before opening any
            // source stream, so this cannot cause a stale source file to be consumed.
            if (!sourceStillUsable && !alreadyApplied)
            {
                stale++;
                continue;
            }

            string retainedSourcePath;
            try { retainedSourcePath = Path.GetFullPath(saved.SourcePath); }
            catch { retainedSourcePath = saved.SourcePath; }

            matches[entry.Path] = new SkeletonSourceMatch(
                entry,
                retainedSourcePath,
                string.Empty,
                false,
                string.IsNullOrWhiteSpace(saved.MatchMethod) ? "Saved DIC match" : saved.MatchMethod,
                saved.SourceRelativePath);
            validStateMatches.Add(saved);
        }

        state.AppliedEntries ??= new List<string>();
        state.Version = CurrentVersion;
        state.BaseName = logs.BaseName;
        state.VolumeIdentifier = inspection.VolumeIdentifier;
        state.SectorCount = inspection.SectorCount;
        state.ExpectedImageSha1 = inspection.ExpectedImageSha1;
        state.Matches = validStateMatches;

        return new DicRecoveryStateLoadResult(state, matches, stale);
    }

    public async Task SaveAsync(
        string statePath,
        DicRecoveryState state,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        IReadOnlySet<string>? appliedEntries = null,
        CancellationToken cancellationToken = default)
    {
        state.Version = CurrentVersion;
        state.AppliedEntries = (appliedEntries ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.Matches = matches.Values
            .OrderBy(m => m.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(m => new DicRecoveryStateMatch
            {
                EntryPath = m.Entry.Path,
                SourcePath = Path.GetFullPath(m.SourcePath),
                Length = m.Entry.DataLength,
                MatchMethod = m.MatchMethod,
                SourceRelativePath = m.SourceRelativePath,
                Applied = appliedEntries?.Contains(m.Entry.Path) == true
            })
            .ToList();

        string temp = statePath + ".tmp";
        string? directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, statePath, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public void Clear(string statePath)
    {
        if (File.Exists(statePath))
            File.Delete(statePath);
    }

    private static bool IsSameRecovery(
        DicRecoveryState state,
        DicLogSet logs,
        SkeletonInspectionResult inspection)
    {
        if (state.Version != CurrentVersion ||
            !state.BaseName.Equals(logs.BaseName, StringComparison.OrdinalIgnoreCase) ||
            state.SectorCount != inspection.SectorCount ||
            !state.VolumeIdentifier.Equals(inspection.VolumeIdentifier, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(state.ExpectedImageSha1) &&
            !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1) &&
            !state.ExpectedImageSha1.Equals(inspection.ExpectedImageSha1, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
