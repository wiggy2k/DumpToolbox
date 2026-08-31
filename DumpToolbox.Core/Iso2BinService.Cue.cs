using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class Iso2BinService
{
    private static async Task<CueSheet> ParseCueAsync(string cuePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cuePath))
            throw new ArgumentException("Choose a CUE file.", nameof(cuePath));

        string fullCue = Path.GetFullPath(cuePath);
        if (!File.Exists(fullCue))
            throw new FileNotFoundException("CUE file not found.", fullCue);

        string[] lines = await File.ReadAllLinesAsync(fullCue, cancellationToken);
        var files = new List<CueFileEntry>();
        var tracks = new List<CueTrack>();
        CueFileEntry? currentFile = null;
        CueTrack? currentTrack = null;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[lineIndex];

            Match fileMatch = FileRegex.Match(line);
            if (fileMatch.Success)
            {
                string name = fileMatch.Groups["name"].Value;
                string type = fileMatch.Groups["type"].Value.ToUpperInvariant();
                if (type is not "BINARY" and not "WAVE")
                    throw new NotSupportedException(
                        $"CUE FILE type '{type}' is not supported. Supported FILE types are BINARY and WAVE.");

                currentFile = new CueFileEntry(name, type, lineIndex);
                files.Add(currentFile);
                currentTrack = null;
                continue;
            }

            Match trackMatch = TrackRegex.Match(line);
            if (trackMatch.Success)
            {
                if (currentFile is null)
                    throw new InvalidOperationException($"CUE line {lineIndex + 1}: TRACK appears before any FILE entry.");

                int number = int.Parse(trackMatch.Groups["number"].Value);
                string type = trackMatch.Groups["type"].Value.ToUpperInvariant();
                CueTrackKind kind = ParseTrackKind(type);
                if (currentFile.OriginalType == "WAVE" && kind != CueTrackKind.Audio)
                    throw new NotSupportedException($"Track {number:00}: FILE ... WAVE can only contain AUDIO tracks.");

                currentTrack = new CueTrack(number, type, kind, lineIndex, currentFile);
                currentFile.Tracks.Add(currentTrack);
                tracks.Add(currentTrack);
                continue;
            }

            Match indexMatch = IndexRegex.Match(line);
            if (indexMatch.Success)
            {
                if (currentTrack is null)
                    throw new InvalidOperationException($"CUE line {lineIndex + 1}: INDEX appears before any TRACK.");

                int indexNumber = int.Parse(indexMatch.Groups["number"].Value);
                string time = indexMatch.Groups["time"].Value;
                currentTrack.Indexes.Add(new CueIndex(indexNumber, ParseCueTime(time), time, lineIndex));
            }
        }

        if (files.Count == 0)
            throw new InvalidOperationException("The CUE does not contain a FILE entry.");
        if (tracks.Count == 0)
            throw new InvalidOperationException("The CUE contains no TRACK entries.");

        foreach (CueFileEntry file in files)
        {
            if (file.Tracks.Count == 0)
                throw new InvalidOperationException($"CUE FILE '{file.OriginalName}' has no TRACK entries.");

            foreach (CueTrack track in file.Tracks)
            {
                if (track.Indexes.Count == 0)
                    throw new InvalidOperationException($"Track {track.Number:00} has no INDEX entry.");
                if (!track.Indexes.Any(i => i.Number == 1))
                    throw new InvalidOperationException($"Track {track.Number:00} has no INDEX 01 entry.");
            }

            for (int i = 1; i < file.Tracks.Count; i++)
            {
                if (file.Tracks[i].EarliestFrame < file.Tracks[i - 1].EarliestFrame)
                    throw new InvalidOperationException(
                        $"Track INDEX positions within FILE '{file.OriginalName}' are not in ascending order.");
            }
        }

        return new CueSheet(fullCue, lines, files, tracks);
    }

    private static async Task<CueLayout> BuildCueLayoutAsync(CueSheet sheet, CancellationToken cancellationToken)
    {
        var result = new List<CueTrackInspection>(sheet.Tracks.Count);
        long totalSectors = 0;
        long inputBytes = 0;
        string cueDirectory = Path.GetDirectoryName(sheet.CuePath) ?? Directory.GetCurrentDirectory();

        foreach (CueFileEntry file in sheet.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string cueStyleName = file.OriginalName
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            file.FullPath = Path.GetFullPath(Path.Combine(cueDirectory, cueStyleName));
            if (!File.Exists(file.FullPath))
                throw new FileNotFoundException($"CUE source file '{file.OriginalName}' was not found.", file.FullPath);

            file.OutputStartFrame = totalSectors;
            bool looksLikeWave = file.OriginalType == "WAVE" ||
                Path.GetExtension(file.FullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

            if (looksLikeWave)
            {
                if (file.Tracks.Any(t => t.Kind != CueTrackKind.Audio))
                    throw new NotSupportedException($"WAVE source '{file.OriginalName}' can only contain AUDIO tracks.");

                WaveInfo wave = await InspectWaveAsync(file.FullPath, cancellationToken);
                file.IsWave = true;
                file.PayloadOffset = wave.DataOffset;
                file.PayloadLength = wave.DataLength;
            }
            else
            {
                file.IsWave = false;
                file.PayloadOffset = 0;
                file.PayloadLength = new FileInfo(file.FullPath).Length;
                if (file.PayloadLength <= 0)
                    throw new InvalidOperationException($"CUE source file '{file.OriginalName}' is empty.");
            }

            long localSourceBytes = 0;
            long fileSectors = 0;

            for (int i = 0; i < file.Tracks.Count; i++)
            {
                CueTrack track = file.Tracks[i];
                long boundaryFrame = i == 0 ? 0 : track.EarliestFrame;
                long sectorCount;

                if (i < file.Tracks.Count - 1)
                {
                    long nextBoundaryFrame = file.Tracks[i + 1].EarliestFrame;
                    sectorCount = nextBoundaryFrame - boundaryFrame;
                    if (sectorCount < 0)
                        throw new InvalidOperationException($"Track {track.Number:00} has an invalid negative length in the CUE.");
                }
                else
                {
                    int sectorSize = SectorSize(track.Kind);
                    long remaining = file.PayloadLength - localSourceBytes;
                    if (remaining < 0)
                        throw new InvalidOperationException($"The CUE layout for '{file.OriginalName}' is larger than its source payload.");
                    if (remaining % sectorSize != 0)
                    {
                        throw new InvalidOperationException(
                            $"The final track in '{file.OriginalName}' ({track.Number:00} {track.OriginalType}) has " +
                            $"{remaining:N0} remaining payload bytes, which is not a multiple of {sectorSize:N0} bytes per sector.");
                    }
                    sectorCount = remaining / sectorSize;
                }

                int sourceSectorSize = SectorSize(track.Kind);
                long sourceBytes = checked(sectorCount * sourceSectorSize);
                if (localSourceBytes + sourceBytes > file.PayloadLength)
                {
                    throw new InvalidOperationException(
                        $"Track {track.Number:00} extends beyond source file '{file.OriginalName}'.");
                }

                result.Add(new CueTrackInspection(
                    track.Number,
                    track.OriginalType,
                    OutputTrackType(track.Kind),
                    file.OutputStartFrame + boundaryFrame,
                    sectorCount,
                    file.PayloadOffset + localSourceBytes,
                    sourceBytes,
                    sourceSectorSize == CookedSectorSize,
                    file.FullPath,
                    file.IsWave ? "WAVE" : file.OriginalType));

                localSourceBytes += sourceBytes;
                fileSectors += sectorCount;
            }

            if (localSourceBytes != file.PayloadLength)
            {
                throw new InvalidOperationException(
                    $"The CUE layout accounts for {localSourceBytes:N0} payload bytes in '{file.OriginalName}', " +
                    $"but its usable payload contains {file.PayloadLength:N0} bytes.");
            }

            totalSectors += fileSectors;
            inputBytes += file.PayloadLength;
        }

        if (totalSectors <= 0)
            throw new InvalidOperationException("The CUE does not describe any sectors.");

        return new CueLayout(result, inputBytes, totalSectors);
    }

    private static string GenerateOutputCue(CueSheet sheet, string outputFileName)
    {
        var output = new StringBuilder();
        var trackByLine = sheet.Tracks.ToDictionary(t => t.TrackLineIndex);
        var indexByLine = sheet.Tracks
            .SelectMany(t => t.Indexes.Select(index => (Track: t, Index: index)))
            .ToDictionary(x => x.Index.LineIndex);
        bool wroteFile = false;

        for (int lineIndex = 0; lineIndex < sheet.OriginalLines.Length; lineIndex++)
        {
            string originalLine = sheet.OriginalLines[lineIndex];

            if (FileRegex.IsMatch(originalLine))
            {
                if (!wroteFile)
                {
                    output.Append("FILE \"")
                        .Append(outputFileName.Replace("\"", string.Empty))
                        .AppendLine("\" BINARY");
                    wroteFile = true;
                }
                continue;
            }

            Match trackMatch = TrackRegex.Match(originalLine);
            if (trackMatch.Success && trackByLine.TryGetValue(lineIndex, out CueTrack? track))
            {
                string indent = trackMatch.Groups["indent"].Value;
                string rest = trackMatch.Groups["rest"].Value;
                output.Append(indent)
                    .Append("TRACK ")
                    .Append(track.Number.ToString("00"))
                    .Append(' ')
                    .Append(OutputTrackType(track.Kind))
                    .Append(rest)
                    .AppendLine();
                continue;
            }

            if (IndexRegex.IsMatch(originalLine) && indexByLine.TryGetValue(lineIndex, out var indexed))
            {
                int whitespace = 0;
                while (whitespace < originalLine.Length && char.IsWhiteSpace(originalLine[whitespace]))
                    whitespace++;
                string indent = originalLine[..whitespace];
                long absoluteFrame = checked(indexed.Track.File.OutputStartFrame + indexed.Index.Frame);
                output.Append(indent)
                    .Append("INDEX ")
                    .Append(indexed.Index.Number.ToString("00"))
                    .Append(' ')
                    .Append(FormatCueTime(absoluteFrame))
                    .AppendLine();
                continue;
            }

            output.AppendLine(originalLine);
        }

        return output.ToString();
    }

    private static async Task<WaveInfo> InspectWaveAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = OpenRead(path, FileOptions.Asynchronous | FileOptions.RandomAccess, 64 * 1024);
        if (input.Length < 12)
            throw new InvalidOperationException($"WAVE file '{Path.GetFileName(path)}' is too small to contain a RIFF/WAVE header.");

        byte[] header = new byte[12];
        await ReadExactlyAsync(input, header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidOperationException($"'{Path.GetFileName(path)}' is not a RIFF/WAVE file.");

        WaveFormat? format = null;
        long dataOffset = -1;
        long dataLength = -1;
        byte[] chunkHeader = new byte[8];

        while (input.Position + 8 <= input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadExactlyAsync(input, chunkHeader, cancellationToken);
            string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            long chunkDataOffset = input.Position;
            long nextChunk = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
            if (nextChunk > input.Length)
                throw new InvalidOperationException($"WAVE file '{Path.GetFileName(path)}' contains a truncated '{chunkId}' chunk.");

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16 || chunkSize > 65536)
                    throw new InvalidOperationException($"WAVE file '{Path.GetFileName(path)}' has an invalid fmt chunk.");

                byte[] fmt = new byte[(int)chunkSize];
                await ReadExactlyAsync(input, fmt, cancellationToken);
                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
                ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12, 2));
                ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

                bool pcm = formatTag == 1;
                if (formatTag == 0xFFFE && fmt.Length >= 40)
                {
                    var subFormat = new Guid(fmt.AsSpan(24, 16));
                    pcm = subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
                }

                format = new WaveFormat(pcm, channels, sampleRate, blockAlign, bitsPerSample);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataOffset;
                dataLength = chunkSize;
            }

            input.Position = nextChunk;
        }

        if (format is null)
            throw new InvalidOperationException($"WAVE file '{Path.GetFileName(path)}' has no fmt chunk.");
        if (dataOffset < 0 || dataLength < 0)
            throw new InvalidOperationException($"WAVE file '{Path.GetFileName(path)}' has no data chunk.");
        if (!format.Pcm || format.Channels != 2 || format.SampleRate != 44100 || format.BitsPerSample != 16 || format.BlockAlign != 4)
        {
            throw new InvalidOperationException(
                $"WAVE file '{Path.GetFileName(path)}' is not CD-DA PCM (required: 44,100 Hz, 16-bit, stereo PCM).");
        }
        if (dataLength == 0 || dataLength % RawSectorSize != 0)
        {
            throw new InvalidOperationException(
                $"WAVE PCM data in '{Path.GetFileName(path)}' is {dataLength:N0} bytes; it must be an exact multiple of {RawSectorSize} bytes.");
        }

        return new WaveInfo(dataOffset, dataLength);
    }

    private static string FormatCueTime(long frame)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        long minutes = frame / (75 * 60);
        long remainder = frame % (75 * 60);
        long seconds = remainder / 75;
        long frames = remainder % 75;
        return $"{minutes:00}:{seconds:00}:{frames:00}";
    }

    private static StringComparer StringComparerForPaths() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static CueTrackKind ParseTrackKind(string type) => type.ToUpperInvariant() switch
    {
        "AUDIO" => CueTrackKind.Audio,
        "MODE1/2048" => CueTrackKind.Mode1_2048,
        "MODE1/2352" => CueTrackKind.Mode1_2352,
        "MODE2/2048" => CueTrackKind.Mode2_2048,
        "MODE2/2352" => CueTrackKind.Mode2_2352,
        _ => throw new NotSupportedException(
            $"CUE track type '{type}' is not supported. Supported types are AUDIO, MODE1/2048, MODE1/2352, MODE2/2048 and MODE2/2352.")
    };

    private static int SectorSize(CueTrackKind kind) => kind switch
    {
        CueTrackKind.Mode1_2048 or CueTrackKind.Mode2_2048 => CookedSectorSize,
        _ => RawSectorSize
    };

    private static string OutputTrackType(CueTrackKind kind) => kind switch
    {
        CueTrackKind.Audio => "AUDIO",
        CueTrackKind.Mode1_2048 or CueTrackKind.Mode1_2352 => "MODE1/2352",
        CueTrackKind.Mode2_2048 or CueTrackKind.Mode2_2352 => "MODE2/2352",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static int ParseCueTime(string value)
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
}
