using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed record CueTrackAnalysis(
    int Number,
    string Type,
    bool IsAudio,
    string FileName,
    string FileType,
    int? Index00Frames,
    int Index01Frames,
    int? ExplicitPregapFrames)
{
    public int FileBackedPregapFrames =>
        Index00Frames is int index00 && Index01Frames >= index00
            ? Index01Frames - index00
            : 0;
}

public sealed record CueSheetAnalysis(
    string CuePath,
    IReadOnlyList<CueTrackAnalysis> Tracks,
    bool HasAudio,
    bool HasData,
    bool IsAudioOnly,
    bool IsMixedMode,
    int FirstAudioTrackNumber,
    int LastAudioTrackNumber,
    string Description)
{
    public CueTrackAnalysis? FindTrack(int number) => Tracks.FirstOrDefault(t => t.Number == number);
}

/// <summary>
/// Lightweight CUE parser used by FindCRCs. Unlike the ISO2BIN CUE inspector,
/// this deliberately does not require the referenced track files to exist: the
/// CUE is being used as a disc-layout description for classifying targets and
/// locating file-backed pregaps.
/// </summary>
public sealed class CueSheetAnalysisService
{
    private static readonly Regex FileRegex = new(
        "^\\s*FILE\\s+\\\"(?<name>[^\\\"]+)\\\"\\s+(?<type>\\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrackRegex = new(
        "^\\s*TRACK\\s+(?<number>\\d+)\\s+(?<type>\\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IndexRegex = new(
        "^\\s*INDEX\\s+(?<number>\\d+)\\s+(?<time>\\d+:\\d{2}:\\d{2})\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PregapRegex = new(
        "^\\s*PREGAP\\s+(?<time>\\d+:\\d{2}:\\d{2})\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<CueSheetAnalysis> AnalyzeAsync(string cuePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cuePath))
            throw new ArgumentException("Choose a CUE sheet.", nameof(cuePath));

        string fullPath = Path.GetFullPath(cuePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("CUE sheet not found.", fullPath);

        string[] lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return AnalyzeLines(lines, fullPath, cancellationToken);
    }

    public Task<CueSheetAnalysis> AnalyzeTextAsync(string cueText, string displayPath, CancellationToken cancellationToken = default)
    {
        if (cueText is null) throw new ArgumentNullException(nameof(cueText));
        string[] lines = cueText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return Task.FromResult(AnalyzeLines(lines, displayPath, cancellationToken));
    }

    private static CueSheetAnalysis AnalyzeLines(IEnumerable<string> lines, string displayPath, CancellationToken cancellationToken)
    {
        var builders = new List<TrackBuilder>();
        string currentFile = string.Empty;
        string currentFileType = "BINARY";
        TrackBuilder? currentTrack = null;

        foreach (string raw in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Match fileMatch = FileRegex.Match(raw);
            if (fileMatch.Success)
            {
                currentFile = fileMatch.Groups["name"].Value;
                currentFileType = fileMatch.Groups["type"].Value.ToUpperInvariant();
                currentTrack = null;
                continue;
            }

            Match trackMatch = TrackRegex.Match(raw);
            if (trackMatch.Success)
            {
                int number = int.Parse(trackMatch.Groups["number"].Value);
                string type = trackMatch.Groups["type"].Value.ToUpperInvariant();
                currentTrack = new TrackBuilder(number, type, currentFile, currentFileType);
                builders.Add(currentTrack);
                continue;
            }

            Match indexMatch = IndexRegex.Match(raw);
            if (indexMatch.Success && currentTrack is not null)
            {
                int indexNumber = int.Parse(indexMatch.Groups["number"].Value);
                int frames = ParseMsf(indexMatch.Groups["time"].Value);
                if (indexNumber == 0)
                    currentTrack.Index00Frames = frames;
                else if (indexNumber == 1)
                    currentTrack.Index01Frames = frames;
                continue;
            }

            Match pregapMatch = PregapRegex.Match(raw);
            if (pregapMatch.Success && currentTrack is not null)
                currentTrack.ExplicitPregapFrames = ParseMsf(pregapMatch.Groups["time"].Value);
        }

        if (builders.Count == 0)
            throw new InvalidOperationException("The CUE contains no TRACK entries.");

        foreach (TrackBuilder builder in builders)
        {
            if (builder.Index01Frames is null)
                throw new InvalidOperationException($"Track {builder.Number:00} has no INDEX 01 entry.");
        }

        CueTrackAnalysis[] tracks = builders
            .Select(b => new CueTrackAnalysis(
                b.Number,
                b.Type,
                b.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase),
                b.FileName,
                b.FileType,
                b.Index00Frames,
                b.Index01Frames!.Value,
                b.ExplicitPregapFrames))
            .ToArray();

        CueTrackAnalysis[] audioTracks = tracks.Where(t => t.IsAudio).ToArray();
        bool hasAudio = audioTracks.Length > 0;
        bool hasData = tracks.Any(t => !t.IsAudio);
        bool mixed = hasAudio && hasData;
        bool audioOnly = hasAudio && !hasData;
        int firstAudio = hasAudio ? audioTracks.Min(t => t.Number) : -1;
        int lastAudio = hasAudio ? audioTracks.Max(t => t.Number) : -1;
        string typeDescription = mixed ? "mixed-mode" : audioOnly ? "audio-only" : "data-only";

        CueTrackAnalysis? track2 = tracks.FirstOrDefault(t => t.Number == 2);
        string pregapDescription = track2 is { IsAudio: true }
            ? track2.FileBackedPregapFrames > 0
                ? $" Track 02 has {track2.FileBackedPregapFrames:N0} file-backed pregap sector(s)."
                : track2.ExplicitPregapFrames is > 0
                    ? $" Track 02 has a synthetic PREGAP of {track2.ExplicitPregapFrames.Value:N0} sector(s), but no INDEX 00 data stored in the image."
                    : " Track 02 has no file-backed INDEX 00 pregap."
            : string.Empty;

        return new CueSheetAnalysis(
            displayPath,
            tracks,
            hasAudio,
            hasData,
            audioOnly,
            mixed,
            firstAudio,
            lastAudio,
            $"CUE: {tracks.Length:N0} track(s), {typeDescription}." + pregapDescription);
    }

    private static int ParseMsf(string value)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int minutes) ||
            !int.TryParse(parts[1], out int seconds) ||
            !int.TryParse(parts[2], out int frames) ||
            minutes < 0 || seconds is < 0 or >= 60 || frames is < 0 or >= 75)
        {
            throw new FormatException($"Invalid CUE time '{value}'.");
        }

        return checked((minutes * 60 + seconds) * 75 + frames);
    }

    private sealed class TrackBuilder
    {
        public TrackBuilder(int number, string type, string fileName, string fileType)
        {
            Number = number;
            Type = type;
            FileName = fileName;
            FileType = fileType;
        }

        public int Number { get; }
        public string Type { get; }
        public string FileName { get; }
        public string FileType { get; }
        public int? Index00Frames { get; set; }
        public int? Index01Frames { get; set; }
        public int? ExplicitPregapFrames { get; set; }
    }
}
