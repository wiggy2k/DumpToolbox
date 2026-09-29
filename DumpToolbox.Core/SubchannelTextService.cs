using System.Globalization;
using System.Text;

namespace DumpToolbox.Core;

public sealed record SubchannelTextProgress(long SectorsProcessed, long TotalSectors)
{
    public double Fraction => TotalSectors == 0 ? 1 : (double)SectorsProcessed / TotalSectors;
}

public enum SubchannelFileFormat
{
    DicDeinterleaved,
    RedumperMultiplexed
}

public sealed record SubchannelTextResult(
    string Text,
    long SectorCount,
    long InputLength,
    SubchannelFileFormat Format)
{
    public string? Mcn { get; init; }
    public IReadOnlyDictionary<int, string> IsrcByTrack { get; init; } =
        new Dictionary<int, string>();
}

public sealed record SubchannelTextFileResult(
    long SectorCount,
    long InputLength,
    long OutputLength,
    SubchannelFileFormat Format)
{
    public string? Mcn { get; init; }
    public IReadOnlyDictionary<int, string> IsrcByTrack { get; init; } =
        new Dictionary<int, string>();
}

public sealed class SubchannelTextService
{
    public const int BytesPerSector = 96;
    public const int RedumperFirstLba = -45150;

    public Task<SubchannelTextResult> RenderAsync(
        string inputPath,
        IProgress<SubchannelTextProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Render(inputPath, progress, cancellationToken), cancellationToken);

    public Task<SubchannelTextFileResult> RenderToFileAsync(
        string inputPath,
        string outputPath,
        IProgress<SubchannelTextProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => RenderToFile(inputPath, outputPath, progress, cancellationToken),
            cancellationToken);

    public static string SuggestOutputPath(string inputPath)
    {
        string fullPath = Path.GetFullPath(inputPath);
        string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, stem + "_subReadable.txt");
    }

    public static string GetFormatDisplayName(SubchannelFileFormat format)
        => format switch
        {
            SubchannelFileFormat.DicDeinterleaved => "DIC/CloneCD de-interleaved",
            SubchannelFileFormat.RedumperMultiplexed => "Redumper multiplexed",
            _ => format.ToString()
        };

    private static SubchannelTextResult Render(
        string inputPath,
        IProgress<SubchannelTextProgress>? progress,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        RenderMetadata metadata = RenderCore(
            inputPath,
            line => output.Append(line),
            progress,
            cancellationToken);

        return new SubchannelTextResult(
            output.ToString(),
            metadata.SectorCount,
            metadata.InputLength,
            metadata.Format)
        {
            Mcn = metadata.Mcn,
            IsrcByTrack = metadata.IsrcByTrack
        };
    }

    private static SubchannelTextFileResult RenderToFile(
        string inputPath,
        string outputPath,
        IProgress<SubchannelTextProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Choose an output file first.", nameof(outputPath));

        string fullInputPath = Path.GetFullPath(inputPath);
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The text output cannot overwrite the source subchannel file.");

        try
        {
            using var output = new FileStream(
                fullOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var writer = new StreamWriter(
                output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                64 * 1024,
                leaveOpen: true);

            RenderMetadata metadata = RenderCore(
                fullInputPath,
                line => writer.Write(line),
                progress,
                cancellationToken);
            writer.Flush();
            output.Flush(flushToDisk: true);

            return new SubchannelTextFileResult(
                metadata.SectorCount,
                metadata.InputLength,
                output.Length,
                metadata.Format)
            {
                Mcn = metadata.Mcn,
                IsrcByTrack = metadata.IsrcByTrack
            };
        }
        catch
        {
            try
            {
                if (File.Exists(fullOutputPath))
                    File.Delete(fullOutputPath);
            }
            catch
            {
                // Preserve the original conversion error.
            }
            throw;
        }
    }

    private static RenderMetadata RenderCore(
        string inputPath,
        Action<StringBuilder> emitLine,
        IProgress<SubchannelTextProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Choose a .sub, .subcode, or .subchannel file first.", nameof(inputPath));

        string fullPath = Path.GetFullPath(inputPath);
        using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        long inputLength = input.Length;
        if (inputLength == 0)
            throw new InvalidDataException("The selected subchannel file is empty.");
        if (inputLength % BytesPerSector != 0)
        {
            throw new InvalidDataException(
                $"The selected file is {inputLength:N0} bytes long. A supported CD subchannel file must contain exactly {BytesPerSector} bytes per sector.");
        }

        long sectorCount = inputLength / BytesPerSector;
        SubchannelFileFormat format = DetectFormat(fullPath, input, sectorCount, cancellationToken);
        var line = new StringBuilder(256);
        var state = new ParserState();
        byte[] storedSector = new byte[BytesPerSector];
        byte[] normalizedSector = new byte[BytesPerSector];

        progress?.Report(new SubchannelTextProgress(0, sectorCount));
        for (long sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(input, storedSector);

            ReadOnlySpan<byte> sector = storedSector;
            if (format == SubchannelFileFormat.RedumperMultiplexed)
            {
                AlignRowSubcode(normalizedSector, storedSector);
                sector = normalizedSector;
            }

            state.Update(sector);
            int displayLba = format == SubchannelFileFormat.RedumperMultiplexed
                ? checked((int)(RedumperFirstLba + sectorIndex))
                : state.Lba;
            line.Clear();
            AppendSector(line, sector, state, displayLba);
            emitLine(line);

            long completed = sectorIndex + 1;
            if ((completed & 0x3ff) == 0 || completed == sectorCount)
                progress?.Report(new SubchannelTextProgress(completed, sectorCount));
        }

        return new RenderMetadata(
            sectorCount,
            inputLength,
            format,
            state.GetMcn(),
            state.GetIsrcByTrack());
    }

    private static SubchannelFileFormat DetectFormat(
        string fullPath,
        FileStream input,
        long sectorCount,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(fullPath);
        if (extension.Equals(".subcode", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".subchannel", StringComparison.OrdinalIgnoreCase))
            return SubchannelFileFormat.RedumperMultiplexed;
        if (extension.Equals(".sub", StringComparison.OrdinalIgnoreCase))
            return SubchannelFileFormat.DicDeinterleaved;

        const int maximumSamples = 64;
        int sampleCount = (int)Math.Min(sectorCount, maximumSamples);
        int deinterleavedScore = 0;
        int multiplexedScore = 0;
        byte[] storedSector = new byte[BytesPerSector];
        byte[] normalizedSector = new byte[BytesPerSector];

        for (int sample = 0; sample < sampleCount; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sectorIndex = sampleCount == 1
                ? 0
                : sample * (sectorCount - 1) / (sampleCount - 1);
            input.Position = sectorIndex * BytesPerSector;
            ReadExactly(input, storedSector);

            deinterleavedScore += ScoreDeinterleavedSector(storedSector);
            AlignRowSubcode(normalizedSector, storedSector);
            multiplexedScore += ScoreDeinterleavedSector(normalizedSector);
        }

        input.Position = 0;
        return multiplexedScore > deinterleavedScore
            ? SubchannelFileFormat.RedumperMultiplexed
            : SubchannelFileFormat.DicDeinterleaved;
    }

    private static int ScoreDeinterleavedSector(ReadOnlySpan<byte> sector)
    {
        int score = IsUniformPChannel(sector) ? 2 : 0;
        ReadOnlySpan<byte> q = sector.Slice(12, 12);
        bool qIsEmpty = true;
        for (int i = 0; i < q.Length; i++)
            qIsEmpty &= q[i] == 0;
        if (qIsEmpty)
            return score;

        int adr = q[0] & 0x0f;
        if (adr is 1 or 2 or 3 or 5 or 6 or 0x0c)
            score += 3;

        int control = (q[0] >> 4) & 0x0f;
        if (control is 0 or 1 or 2 or 3 or 4 or 6 or 8 or 9 or 0x0a or 0x0b)
            score++;

        if (adr == 1 && IsBcd(q[1]) && IsBcd(q[2]) &&
            IsBcd(q[3]) && IsBcd(q[4]) && IsBcd(q[5]) &&
            IsBcd(q[7]) && IsBcd(q[8]) && IsBcd(q[9]))
            score += 4;

        return score;
    }

    private static bool IsUniformPChannel(ReadOnlySpan<byte> sector)
    {
        byte expected = sector[0];
        if (expected is not 0x00 and not 0xff)
            return false;

        for (int i = 1; i < 12; i++)
        {
            if (sector[i] != expected)
                return false;
        }
        return true;
    }

    private static bool IsBcd(byte value)
        => (value & 0x0f) <= 9 && ((value >> 4) & 0x0f) <= 9;

    private static void ReadExactly(Stream input, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = input.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("The subchannel file changed or ended while it was being read.");
            offset += read;
        }
    }

    private static void AppendSector(
        StringBuilder output,
        ReadOnlySpan<byte> subcode,
        ParserState state,
        int lba)
    {
        output.Append("LBA[");
        AppendDecimalLba(output, lba);
        output.Append(", ");
        AppendHexLba(output, lba);
        output.Append("]: P[");
        AppendHexByte(output, subcode[0]);
        output.Append("], Q[");
        for (int i = 12; i < 24; i++)
            AppendHexByte(output, subcode[i]);
        output.Append("]{");

        AppendControlDescription(output, (subcode[12] >> 4) & 0x0f);
        AppendAdrDescription(output, subcode, state);
        output.Append("}, RtoW[");
        AppendRtoW(output, subcode);
        output.Append("]\r\n");
    }

    private static void AppendDecimalLba(StringBuilder output, int lba)
    {
        if (lba >= 0)
        {
            output.Append(lba.ToString("D6", CultureInfo.InvariantCulture));
            return;
        }

        output.Append('-');
        output.Append(Math.Abs((long)lba).ToString("D5", CultureInfo.InvariantCulture));
    }

    private static void AppendHexLba(StringBuilder output, int lba)
    {
        if (lba == 0)
        {
            output.Append("0000000");
            return;
        }

        output.Append("0x");
        if (lba > 0)
            output.Append(lba.ToString("x5", CultureInfo.InvariantCulture));
        else
            output.Append(unchecked((uint)lba).ToString("x", CultureInfo.InvariantCulture));
    }

    private static void AppendHexByte(StringBuilder output, byte value)
        => output.Append(value.ToString("x2", CultureInfo.InvariantCulture));

    private static void AppendControlDescription(StringBuilder output, int control)
    {
        output.Append(control switch
        {
            0x0 => "Audio, 2ch, Copy NG, Pre-emphasis No, ",
            0x1 => "Audio, 2ch, Copy NG, Pre-emphasis Yes, ",
            0x2 => "Audio, 2ch, Copy OK, Pre-emphasis No, ",
            0x3 => "Audio, 2ch, Copy OK, Pre-emphasis Yes, ",
            0x4 => " Data,      Copy NG,                  ",
            0x6 => " Data,      Copy OK,                  ",
            0x8 => "Audio, 4ch, Copy NG, Pre-emphasis No, ",
            0x9 => "Audio, 4ch, Copy NG, Pre-emphasis Yes, ",
            0xa => "Audio, 4ch, Copy OK, Pre-emphasis No, ",
            0xb => "Audio, 4ch, Copy OK, Pre-emphasis Yes, ",
            _ => "Unknown,                              "
        });
    }

    private static void AppendAdrDescription(StringBuilder output, ReadOnlySpan<byte> q, ParserState state)
    {
        int adr = q[12] & 0x0f;
        switch (adr)
        {
            case 1:
                AppendCurrentPosition(output, q);
                break;
            case 2:
                output.Append("MediaCatalogNumber [");
                output.Append(state.MediaCatalogue.PadLeft(13));
                output.Append("], AMSF[     :");
                AppendHexByte(output, q[21]);
                output.Append(']');
                break;
            case 3:
                output.Append("ItnStdRecordingCode [");
                output.Append(state.GetCurrentIsrc().PadLeft(12));
                output.Append("], AMSF[     :");
                AppendHexByte(output, q[21]);
                output.Append(']');
                break;
            case 5:
                AppendAdr5(output, q);
                break;
            case 6:
                output.Append("Unknown Data    [");
                for (int i = 13; i <= 20; i++)
                    AppendHexByte(output, q[i]);
                output.Append("], AMSF[     :");
                AppendHexByte(output, q[21]);
                output.Append(']');
                break;
            case 0x0c:
                if (q[13] == 0 && q[14] == 0xb1)
                {
                    output.Append("Track[");
                    AppendHexByte(output, q[13]);
                    output.Append("], Point[");
                    AppendHexByte(output, q[14]);
                    output.Append("], 15[");
                    AppendHexByte(output, q[15]);
                    output.Append("], 16[");
                    AppendHexByte(output, q[16]);
                    output.Append("], 17[");
                    AppendHexByte(output, q[17]);
                    output.Append("], 18[");
                    AppendHexByte(output, q[18]);
                    output.Append("], 19[");
                    AppendHexByte(output, q[19]);
                    output.Append("], 20[");
                    AppendHexByte(output, q[20]);
                    output.Append("], 21[");
                    AppendHexByte(output, q[21]);
                    output.Append(']');
                }
                break;
            default:
                output.Append("Adr[");
                AppendHexByte(output, q[12]);
                output.Append("], Track[");
                AppendHexByte(output, q[13]);
                output.Append("], Idx[");
                AppendHexByte(output, q[14]);
                output.Append("], RMSF[");
                AppendMsf(output, q[15], q[16], q[17]);
                output.Append("], AMSF[");
                AppendMsf(output, q[19], q[20], q[21]);
                output.Append(']');
                break;
        }
    }

    private static void AppendCurrentPosition(StringBuilder output, ReadOnlySpan<byte> q)
    {
        if (q[13] != 0)
        {
            output.Append("Track[");
            AppendHexByte(output, q[13]);
            output.Append("], Idx[");
            AppendHexByte(output, q[14]);
            output.Append("], RMSF[");
            AppendMsf(output, q[15], q[16], q[17]);
            output.Append("], AMSF[");
            AppendMsf(output, q[19], q[20], q[21]);
            output.Append(']');
            return;
        }

        output.Append("Track[");
        AppendHexByte(output, q[13]);
        output.Append("], Point[");
        AppendHexByte(output, q[14]);
        output.Append("], AMSF[");
        AppendMsf(output, q[15], q[16], q[17]);

        switch (q[14])
        {
            case 0xa0:
                output.Append("], TrackNumOf1stTrack[");
                AppendHexByte(output, q[19]);
                output.Append("], ProgramAreaFormat[");
                AppendHexByte(output, q[20]);
                output.Append(']');
                break;
            case 0xa1:
                output.Append("], TrackNumOfLastTrack[");
                AppendHexByte(output, q[19]);
                output.Append(']');
                break;
            case 0xa2:
                output.Append("], StartTimeOfLead-out[");
                AppendMsf(output, q[19], q[20], q[21]);
                output.Append(']');
                break;
            default:
                output.Append("], StartTimeOfTrack[");
                AppendMsf(output, q[19], q[20], q[21]);
                output.Append(']');
                break;
        }
    }

    private static void AppendAdr5(StringBuilder output, ReadOnlySpan<byte> q)
    {
        if (q[13] == 0)
        {
            output.Append("Track[");
            AppendHexByte(output, q[13]);
            output.Append("], Point[");
            AppendHexByte(output, q[14]);

            switch (q[14])
            {
                case 0xb0:
                    output.Append("], StartTimeForTheNextSession[");
                    AppendMsf(output, q[15], q[16], q[17]);
                    output.Append("], NumberOfDifferentMode-5[");
                    AppendHexByte(output, q[18]);
                    output.Append("], OutermostLead-out[");
                    AppendMsf(output, q[19], q[20], q[21]);
                    output.Append(']');
                    break;
                case 0xb1:
                    output.Append("], NumberOfSkipIntervalPointers[");
                    AppendHexByte(output, q[19]);
                    output.Append("], NumberOfSkipTrackAssignmentsInPoint[");
                    AppendHexByte(output, q[20]);
                    output.Append(']');
                    break;
                case 0xb2:
                case 0xb3:
                case 0xb4:
                    output.Append("], TrackNumberToSkipUponPlayback[");
                    AppendSpacedBytes(output, q.Slice(15, 7));
                    output.Append(']');
                    break;
                case 0xc0:
                    output.Append("], OptimumRecordingPower[");
                    AppendHexByte(output, q[15]);
                    output.Append("], StartTimeOfTheFirstLead-in[");
                    AppendMsf(output, q[19], q[20], q[21]);
                    output.Append(']');
                    break;
                case 0xc1:
                    output.Append("], CopyOfInfoFromA1Point[");
                    AppendSpacedBytes(output, q.Slice(15, 7));
                    output.Append(']');
                    break;
                default:
                    output.Append("], SkipIntervalStopTime[");
                    AppendMsf(output, q[15], q[16], q[17]);
                    output.Append("], SkipIntervalStartTime[");
                    AppendMsf(output, q[19], q[20], q[21]);
                    output.Append(']');
                    break;
            }
        }
        else if (q[13] == 0xaa)
        {
            output.Append("Track[");
            AppendHexByte(output, q[13]);
            output.Append("], Point[");
            AppendHexByte(output, q[14]);
            output.Append("], StartTime[");
            AppendMsf(output, q[19], q[20], q[21]);
            output.Append(']');
        }
    }

    private static void AppendMsf(StringBuilder output, byte minute, byte second, byte frame)
    {
        AppendHexByte(output, minute);
        output.Append(':');
        AppendHexByte(output, second);
        output.Append(':');
        AppendHexByte(output, frame);
    }

    private static void AppendSpacedBytes(StringBuilder output, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
                output.Append(' ');
            AppendHexByte(output, bytes[i]);
        }
    }

    private static void AppendRtoW(StringBuilder output, ReadOnlySpan<byte> subcode)
    {
        Span<byte> column = stackalloc byte[BytesPerSector];
        AlignColumnSubcode(column, subcode);

        for (int pack = 0; pack < 4; pack++)
        {
            int offset = pack * 24;
            int command = column[offset] & 0x3f;
            int instruction = column[offset + 1] & 0x3f;
            AppendRtoWCommand(output, command, instruction);
            if (pack < 3)
                output.Append(", ");
        }
    }

    private static void AlignColumnSubcode(Span<byte> column, ReadOnlySpan<byte> row)
    {
        column.Clear();
        int rowIndex = 0;
        int mask = 0x80;
        for (int bitNumber = 0; bitNumber < 8; bitNumber++)
        {
            for (int columnIndex = 0; columnIndex < BytesPerSector; rowIndex++)
            {
                for (int shift = 0; shift < 8; shift++, columnIndex++)
                {
                    int distance = shift - bitNumber;
                    int value = distance > 0 ? row[rowIndex] << distance : row[rowIndex] >> -distance;
                    column[columnIndex] |= (byte)(value & mask);
                }
            }
            mask >>= 1;
        }
    }

    private static void AlignRowSubcode(Span<byte> row, ReadOnlySpan<byte> column)
    {
        row.Clear();
        for (int channel = 0; channel < 8; channel++)
        {
            int sourceMask = 0x80 >> channel;
            int rowOffset = channel * 12;
            for (int bit = 0; bit < BytesPerSector; bit++)
            {
                if ((column[bit] & sourceMask) != 0)
                    row[rowOffset + bit / 8] |= (byte)(0x80 >> bit % 8);
            }
        }
    }

    private static void AppendRtoWCommand(StringBuilder output, int command, int instruction)
    {
        switch (command)
        {
            case 0:
                output.Append('0');
                return;
            case 8:
                output.Append("LINE-GRAPHICS");
                output.Append(instruction switch
                {
                    4 => "->Write FONT",
                    12 => "->Soft scroll SCREEN",
                    _ => $"->Unknown[{instruction:D2}]"
                });
                return;
            case 9:
                output.Append("TV-GRAPHICS");
                output.Append(instruction switch
                {
                    1 => "->Preset MEMORY",
                    2 => "->Preset BORDER",
                    6 => "->Write FONT FORE/BACKGROUND",
                    20 => "->Soft scroll SCREEN with preset",
                    24 => "->Soft scroll SCREEN with copy",
                    28 => "->Define color transparency",
                    30 => "->Load CLUT 0 .. 7",
                    31 => "->Load CLUT 8 .. 15",
                    38 => "->XOR FONT with 2 colors",
                    _ => $"->Unknown[{instruction:D2}]"
                });
                return;
            case 10:
                output.Append("Extended-TV-Graphics");
                AppendExtendedTvInstruction(output, instruction);
                return;
            case 17:
            case 18:
            case 19:
            case 21:
            case 22:
            case 23:
            case 32:
                output.Append("CD TEXT");
                return;
            case 24:
                output.Append("MIDI");
                return;
            case 56:
                output.Append("USER");
                return;
            default:
                output.Append("Unknown[");
                output.Append(command.ToString("D2", CultureInfo.InvariantCulture));
                output.Append(']');
                return;
        }
    }

    private static void AppendExtendedTvInstruction(StringBuilder output, int instruction)
    {
        switch (instruction)
        {
            case 3:
                output.Append("->MEMORY control");
                return;
            case 6:
                output.Append("->Write Additional FONT FORE/BACKGROUND");
                return;
            case 14:
                output.Append("->XOR additional FONT with 2 colors");
                return;
        }

        if (instruction is >= 16 and <= 47)
        {
            int first = (instruction - 16) * 8;
            output.Append($"->Load CLUT {first} .. {first + 7}");
            return;
        }

        if (instruction is >= 48 and <= 58)
        {
            int first = (instruction - 48) * 16;
            output.Append($"->Load CLUT additional {first} .. {first + 15}");
            return;
        }

        if (instruction == 59)
        {
            output.Append("->Load CLUT additional 176 .. 181");
            return;
        }

        if (instruction is >= 60 and <= 63)
        {
            int first = 192 + (instruction - 60) * 16;
            output.Append($"->Load CLUT additional {first} .. {first + 15}");
            return;
        }

        output.Append("->Unknown[");
        output.Append(instruction.ToString("D2", CultureInfo.InvariantCulture));
        output.Append(']');
    }

    private sealed record RenderMetadata(
        long SectorCount,
        long InputLength,
        SubchannelFileFormat Format,
        string? Mcn,
        IReadOnlyDictionary<int, string> IsrcByTrack);

    private sealed class ParserState
    {
        private readonly Dictionary<string, int> _mcnCandidates = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Dictionary<string, int>> _isrcCandidatesByTrack = new();
        private int _previousTrack = 1;
        private int _currentTrack = 1;

        public int Lba { get; private set; }
        public string MediaCatalogue => GetMostFrequent(_mcnCandidates) ?? string.Empty;

        public void Update(ReadOnlySpan<byte> subcode)
        {
            int adr = subcode[12] & 0x0f;
            switch (adr)
            {
                case 1:
                    _currentTrack = BcdToDecimal(subcode[13]);
                    Lba = MsfToLba(
                        BcdToDecimal(subcode[19]),
                        BcdToDecimal(subcode[20]),
                        BcdToDecimal(subcode[21]));
                    if (_currentTrack == 0)
                    {
                        _currentTrack = BcdToDecimal(subcode[14]);
                        Lba = MsfToLba(
                            BcdToDecimal(subcode[15]),
                            BcdToDecimal(subcode[16]),
                            BcdToDecimal(subcode[17]));
                    }
                    break;
                case 2:
                    string catalogue = DecodeMediaCatalogue(subcode);
                    if (HasValidQChannelCrc(subcode) && IsValidMcn(catalogue, subcode))
                        AddCandidate(_mcnCandidates, catalogue);
                    Lba++;
                    break;
                case 3:
                    if (_previousTrack > 0 && HasValidQChannelCrc(subcode))
                    {
                        string isrc = DecodeIsrc(subcode);
                        if (IsValidIsrc(isrc))
                        {
                            if (!_isrcCandidatesByTrack.TryGetValue(_previousTrack, out Dictionary<string, int>? candidates))
                            {
                                candidates = new Dictionary<string, int>(StringComparer.Ordinal);
                                _isrcCandidatesByTrack[_previousTrack] = candidates;
                            }
                            AddCandidate(candidates, isrc);
                        }
                    }
                    Lba++;
                    break;
                case 5:
                    _currentTrack = BcdToDecimal(subcode[13]);
                    if (_currentTrack == 0)
                    {
                        _currentTrack = BcdToDecimal(subcode[14]);
                        Lba = -151;
                    }
                    break;
            }

            _previousTrack = _currentTrack;
        }

        public string GetCurrentIsrc()
            => _isrcCandidatesByTrack.TryGetValue(_currentTrack, out Dictionary<string, int>? candidates)
                ? GetMostFrequent(candidates) ?? string.Empty
                : string.Empty;

        public string? GetMcn() => GetMostFrequent(_mcnCandidates);

        public IReadOnlyDictionary<int, string> GetIsrcByTrack()
        {
            var result = new SortedDictionary<int, string>();
            foreach ((int track, Dictionary<string, int> candidates) in _isrcCandidatesByTrack)
            {
                if (GetMostFrequent(candidates) is { } isrc)
                    result[track] = isrc;
            }
            return result;
        }

        private static int BcdToDecimal(byte value) => ((value >> 4) & 0x0f) * 10 + (value & 0x0f);

        private static int MsfToLba(int minute, int second, int frame)
            => (minute * 60 + second) * 75 + frame - 150;

        private static void AddCandidate(Dictionary<string, int> candidates, string value)
            => candidates[value] = candidates.GetValueOrDefault(value) + 1;

        private static string? GetMostFrequent(Dictionary<string, int> candidates)
        {
            string? selected = null;
            int selectedCount = 0;
            foreach ((string value, int count) in candidates)
            {
                if (count > selectedCount)
                {
                    selected = value;
                    selectedCount = count;
                }
            }
            return selected;
        }

        private static bool IsValidMcn(string mcn, ReadOnlySpan<byte> q)
        {
            if (mcn.Length != 13 || q[20] != 0 || (q[19] & 0x0f) != 0)
                return false;

            bool hasNonZeroDigit = false;
            foreach (char character in mcn)
            {
                if (character is < '0' or > '9')
                    return false;
                hasNonZeroDigit |= character != '0';
            }
            return hasNonZeroDigit;
        }

        private static bool IsValidIsrc(string isrc)
        {
            if (isrc.Length != 12 ||
                isrc[0] is < 'A' or > 'Z' ||
                isrc[1] is < 'A' or > 'Z')
                return false;

            for (int i = 2; i <= 4; i++)
            {
                if (isrc[i] is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9'))
                    return false;
            }
            for (int i = 5; i < isrc.Length; i++)
            {
                if (isrc[i] is < '0' or > '9')
                    return false;
            }
            return true;
        }

        private static bool HasValidQChannelCrc(ReadOnlySpan<byte> subcode)
        {
            ushort crc = 0;
            for (int i = 12; i <= 21; i++)
            {
                crc ^= (ushort)(subcode[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
            }

            crc ^= 0xffff;
            ushort storedCrc = (ushort)(subcode[22] << 8 | subcode[23]);
            return crc == storedCrc;
        }

        private static string DecodeMediaCatalogue(ReadOnlySpan<byte> q)
        {
            Span<char> chars = stackalloc char[13];
            int target = 0;
            for (int i = 13; i <= 18; i++)
            {
                chars[target++] = (char)(((q[i] >> 4) & 0x0f) + '0');
                chars[target++] = (char)((q[i] & 0x0f) + '0');
            }
            chars[target] = (char)(((q[19] >> 4) & 0x0f) + '0');
            return new string(chars);
        }

        private static string DecodeIsrc(ReadOnlySpan<byte> q)
        {
            Span<char> chars = stackalloc char[12];
            chars[0] = (char)(((q[13] >> 2) & 0x3f) + '0');
            chars[1] = (char)((((q[13] << 4) & 0x30) | ((q[14] >> 4) & 0x0f)) + '0');
            chars[2] = (char)((((q[14] << 2) & 0x3c) | ((q[15] >> 6) & 0x03)) + '0');
            chars[3] = (char)((q[15] & 0x3f) + '0');
            chars[4] = (char)(((q[16] >> 2) & 0x3f) + '0');
            chars[5] = (char)(((q[17] >> 4) & 0x0f) + '0');
            chars[6] = (char)((q[17] & 0x0f) + '0');
            chars[7] = (char)(((q[18] >> 4) & 0x0f) + '0');
            chars[8] = (char)((q[18] & 0x0f) + '0');
            chars[9] = (char)(((q[19] >> 4) & 0x0f) + '0');
            chars[10] = (char)((q[19] & 0x0f) + '0');
            chars[11] = (char)(((q[20] >> 4) & 0x0f) + '0');
            return new string(chars);
        }
    }
}
