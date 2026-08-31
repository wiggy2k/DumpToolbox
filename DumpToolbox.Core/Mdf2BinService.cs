using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

public sealed record Mdf2BinTrackInspection(
    int Session,
    int Number,
    string Mode,
    byte ModeCode,
    byte AdrCtl,
    bool HasInterleavedSubchannel,
    ushort StoredSectorSize,
    int MainChannelBytesPerSector,
    uint StartLba,
    ulong MdfIndex01Offset,
    uint ReportedPregapSectors,
    uint StoredPregapSectors,
    uint DataSectors,
    ulong PhysicalStartOffset,
    long OutputIndex00Sector,
    long OutputIndex01Sector,
    long OutputEndSector);

public sealed record Mdf2BinInspection(
    string MdsPath,
    string MdfPath,
    int VersionMajor,
    int VersionMinor,
    string MediumType,
    int SessionCount,
    IReadOnlyList<Mdf2BinTrackInspection> Tracks,
    long OutputSectors,
    long OutputBytes,
    bool HasInterleavedSubchannel,
    bool AllTracksHaveInterleavedSubchannel,
    IReadOnlyList<string> Warnings);

public sealed record Mdf2BinProgress(long SectorsProcessed, long TotalSectors, long InputBytesProcessed)
{
    public double Fraction => TotalSectors <= 0 ? 0 : Math.Clamp((double)SectorsProcessed / TotalSectors, 0, 1);
}

public sealed record Mdf2BinResult(
    string OutputBinPath,
    string OutputCuePath,
    string? OutputSubPath,
    long SectorCount,
    long OutputBytes,
    int TrackCount,
    int SessionCount);

/// <summary>
/// Reads classic Alcohol 120% MDS 1.x CD descriptors and converts their MDF data stream
/// to a conventional 2352-byte BIN plus CUE. The MDS layout is parsed explicitly; no
/// attempt is made to guess track boundaries from the MDF file size.
/// </summary>
public sealed class Mdf2BinService
{
    private static readonly byte[] MdsSignature = Encoding.ASCII.GetBytes("MEDIA DESCRIPTOR");

    private const int HeaderSize = 88;
    private const int SessionSize = 24;
    private const int TrackSize = 80;
    private const int TrackExtraSize = 8;
    private const int FooterSize = 16;
    private const int CdMainChannelSize = 2352;
    private const int CdSubchannelSize = 96;

    private const byte SubchannelNone = 0x00;
    private const byte SubchannelInterleaved = 0x08;

    private const ushort MediumCd = 0x00;
    private const ushort MediumCdr = 0x01;
    private const ushort MediumCdrw = 0x02;

    private const byte TrackNoData = 0x00;
    private const byte TrackDvd = 0x02;
    private const byte TrackAudio = 0xA9;
    private const byte TrackAudioAlt = 0xE9;
    private const byte TrackMode1 = 0xAA;
    private const byte TrackMode1Alt = 0xEA;
    private const byte TrackMode2 = 0xAB;
    private const byte TrackMode2F1 = 0xEC;
    private const byte TrackMode2F2 = 0xED;
    private const byte TrackMode2F1Alt = 0xAC;
    private const byte TrackMode2F2Alt = 0xAD;

    public async Task<Mdf2BinInspection> AnalyzeAsync(
        string mdsPath,
        string? mdfOverridePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mdsPath))
            throw new ArgumentException("Choose an MDS descriptor file.", nameof(mdsPath));

        mdsPath = Path.GetFullPath(mdsPath);
        if (!File.Exists(mdsPath))
            throw new FileNotFoundException("MDS descriptor not found.", mdsPath);

        await using var stream = new FileStream(
            mdsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length < HeaderSize)
            throw new InvalidDataException("The MDS file is too small to contain a valid 88-byte header.");

        byte[] header = new byte[HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken);

        if (!header.AsSpan(0, 16).SequenceEqual(MdsSignature))
            throw new InvalidDataException("The file does not begin with the classic Alcohol 'MEDIA DESCRIPTOR' signature.");

        int versionMajor = header[16];
        int versionMinor = header[17];
        if (versionMajor != 1)
            throw new NotSupportedException($"MDS version {versionMajor}.{versionMinor} is not supported. MDF2BIN currently supports classic MDS 1.x only.");

        ushort mediumCode = ReadUInt16(header, 18);
        string mediumType = mediumCode switch
        {
            MediumCd => "CD",
            MediumCdr => "CD-R",
            MediumCdrw => "CD-RW",
            0x10 => "DVD",
            0x12 => "DVD-R",
            _ => $"Unknown (0x{mediumCode:X4})"
        };

        if (mediumCode is not (MediumCd or MediumCdr or MediumCdrw))
            throw new NotSupportedException($"MDF2BIN currently supports CD/CD-R/CD-RW MDS images only; this descriptor reports {mediumType}.");

        ushort sessionCount = ReadUInt16(header, 20);
        uint sessionOffset = ReadUInt32(header, 80);
        uint discMetadataOffset = ReadUInt32(header, 84);

        if (sessionCount == 0)
            throw new InvalidDataException("The MDS descriptor reports zero sessions.");
        if (sessionOffset < HeaderSize || (ulong)sessionOffset + (ulong)sessionCount * SessionSize > (ulong)stream.Length)
            throw new InvalidDataException("The MDS session table points outside the descriptor file.");

        var sessions = new List<MdsSession>(sessionCount);
        stream.Position = sessionOffset;
        byte[] sessionBuffer = new byte[SessionSize];
        for (int i = 0; i < sessionCount; i++)
        {
            await ReadExactlyAsync(stream, sessionBuffer, cancellationToken);
            var session = new MdsSession(
                SessionStart: ReadInt32(sessionBuffer, 0),
                SessionEnd: ReadInt32(sessionBuffer, 4),
                Sequence: ReadUInt16(sessionBuffer, 8),
                AllBlocks: sessionBuffer[10],
                NonTrackBlocks: sessionBuffer[11],
                FirstTrack: ReadUInt16(sessionBuffer, 12),
                LastTrack: ReadUInt16(sessionBuffer, 14),
                TrackOffset: ReadUInt32(sessionBuffer, 20));

            if (session.AllBlocks == 0)
                throw new InvalidDataException($"Session {session.Sequence} contains no descriptor blocks.");
            if ((ulong)session.TrackOffset + (ulong)session.AllBlocks * TrackSize > (ulong)stream.Length)
                throw new InvalidDataException($"Session {session.Sequence}'s track table points outside the MDS file.");

            sessions.Add(session);
        }

        var tracks = new List<MdsTrack>();
        uint footerOffset = 0;
        byte[] trackBuffer = new byte[TrackSize];
        byte[] extraBuffer = new byte[TrackExtraSize];

        foreach (MdsSession session in sessions.OrderBy(s => s.Sequence))
        {
            stream.Position = session.TrackOffset;
            for (int i = 0; i < session.AllBlocks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadExactlyAsync(stream, trackBuffer, cancellationToken);

                byte point = trackBuffer[4];
                if (point is < 1 or > 99)
                    continue; // A0/A1/A2/B0/C0 etc. TOC descriptor blocks.

                byte mode = trackBuffer[0];
                byte subMode = trackBuffer[1];
                byte adrCtl = trackBuffer[2];
                uint extraOffset = ReadUInt32(trackBuffer, 12);
                ushort sectorSize = ReadUInt16(trackBuffer, 16);
                uint startLba = ReadUInt32(trackBuffer, 36);
                ulong startOffset = ReadUInt64(trackBuffer, 40);
                uint files = ReadUInt32(trackBuffer, 48);
                uint thisFooterOffset = ReadUInt32(trackBuffer, 52);

                if (mode == TrackNoData)
                    continue;
                if (!IsSupportedCdTrackMode(mode))
                    throw new NotSupportedException($"Track {point:00} uses unsupported MDS mode 0x{mode:X2}.");
                if (subMode is not (SubchannelNone or SubchannelInterleaved))
                    throw new NotSupportedException($"Track {point:00} uses unsupported MDS subchannel mode 0x{subMode:X2}.");
                if (files != 1)
                    throw new NotSupportedException($"Track {point:00} reports {files} MDF data file(s). Split/multi-file MDF sets are not supported yet.");
                if (extraOffset == 0 || (ulong)extraOffset + TrackExtraSize > (ulong)stream.Length)
                    throw new InvalidDataException($"Track {point:00} has an invalid TrackExtra pointer.");

                long returnPosition = stream.Position;
                stream.Position = extraOffset;
                await ReadExactlyAsync(stream, extraBuffer, cancellationToken);
                stream.Position = returnPosition;

                uint pregap = ReadUInt32(extraBuffer, 0);
                uint sectors = ReadUInt32(extraBuffer, 4);
                if (sectors == 0)
                    throw new InvalidDataException($"Track {point:00} reports zero data sectors.");

                int mainChannelSize = sectorSize - (subMode == SubchannelInterleaved ? CdSubchannelSize : 0);
                if (mainChannelSize != CdMainChannelSize)
                {
                    throw new NotSupportedException(
                        $"Track {point:00} stores {sectorSize} bytes per MDF sector" +
                        (subMode == SubchannelInterleaved ? " including 96-byte subchannel" : string.Empty) +
                        $", leaving {mainChannelSize} main-channel bytes. MDF2BIN v1 requires raw 2352-byte CD sectors.");
                }

                if (footerOffset == 0 && thisFooterOffset != 0)
                    footerOffset = thisFooterOffset;

                tracks.Add(new MdsTrack(
                    Session: session.Sequence,
                    FirstTrackInSession: session.FirstTrack,
                    Number: point,
                    Mode: mode,
                    SubMode: subMode,
                    AdrCtl: adrCtl,
                    SectorSize: sectorSize,
                    StartLba: startLba,
                    StartOffset: startOffset,
                    Pregap: pregap,
                    Sectors: sectors));
            }
        }

        if (tracks.Count == 0)
            throw new InvalidDataException("No CD track records (POINT 01-99) were found in the MDS descriptor.");

        tracks = tracks
            .OrderBy(t => t.Session)
            .ThenBy(t => t.Number)
            .ToList();

        string mdfPath = ResolveMdfPath(mdsPath, mdfOverridePath, stream, footerOffset, discMetadataOffset);
        if (!File.Exists(mdfPath))
            throw new FileNotFoundException("The MDF data file referenced by the descriptor could not be found. Choose it manually in the MDF field.", mdfPath);

        long mdfLength = new FileInfo(mdfPath).Length;
        var warnings = new List<string>();
        if (sessionCount > 1)
        {
            warnings.Add(
                "This is a multi-session CD. The BIN preserves the MDF track/pregap bytes in track order, but CUE cannot fully encode session lead-in/lead-out structures; REM SESSION markers are written for reference.");
        }

        var inspectedTracks = new List<Mdf2BinTrackInspection>(tracks.Count);
        long outputCursor = 0;
        ulong? previousPhysicalEnd = null;

        foreach (MdsTrack track in tracks)
        {
            bool firstInSession = track.Number == track.FirstTrackInSession;
            uint storedPregap = firstInSession ? 0u : track.Pregap;
            ulong pregapBytes = checked((ulong)storedPregap * track.SectorSize);
            if (track.StartOffset < pregapBytes)
            {
                throw new InvalidDataException(
                    $"Track {track.Number:00} INDEX 01 offset {track.StartOffset:N0} is smaller than its stored pregap ({pregapBytes:N0} bytes).");
            }

            ulong physicalStart = track.StartOffset - pregapBytes;
            ulong physicalLength = checked((ulong)(storedPregap + track.Sectors) * track.SectorSize);
            ulong physicalEnd = checked(physicalStart + physicalLength);
            if (physicalEnd > (ulong)mdfLength)
            {
                throw new InvalidDataException(
                    $"Track {track.Number:00} requires MDF bytes through offset {physicalEnd:N0}, beyond the {mdfLength:N0}-byte data file.");
            }

            if (previousPhysicalEnd.HasValue && physicalStart < previousPhysicalEnd.Value)
                warnings.Add($"Track {track.Number:00} physically overlaps the preceding track region in the MDF; conversion will follow the MDS track records exactly.");
            else if (previousPhysicalEnd.HasValue && physicalStart > previousPhysicalEnd.Value)
                warnings.Add($"There are {physicalStart - previousPhysicalEnd.Value:N0} unreferenced MDF byte(s) before Track {track.Number:00}; they are not copied to BIN.");

            long index00 = outputCursor;
            long index01 = checked(outputCursor + storedPregap);
            long outputEnd = checked(index01 + track.Sectors - 1);

            inspectedTracks.Add(new Mdf2BinTrackInspection(
                Session: track.Session,
                Number: track.Number,
                Mode: FormatTrackMode(track.Mode),
                ModeCode: track.Mode,
                AdrCtl: track.AdrCtl,
                HasInterleavedSubchannel: track.SubMode == SubchannelInterleaved,
                StoredSectorSize: track.SectorSize,
                MainChannelBytesPerSector: CdMainChannelSize,
                StartLba: track.StartLba,
                MdfIndex01Offset: track.StartOffset,
                ReportedPregapSectors: track.Pregap,
                StoredPregapSectors: storedPregap,
                DataSectors: track.Sectors,
                PhysicalStartOffset: physicalStart,
                OutputIndex00Sector: index00,
                OutputIndex01Sector: index01,
                OutputEndSector: outputEnd));

            outputCursor = checked(outputEnd + 1);
            previousPhysicalEnd = physicalEnd;

            if (track.Number == 1 && track.StartLba > 0)
            {
                warnings.Add(
                    $"Track 01 reports start LBA {track.StartLba:N0}. Classic MDS cannot preserve hidden-track audio before Track 01 INDEX 01 (HTOA), so any such missing pregap audio cannot be reconstructed from this image format.");
            }

            if (firstInSession && track.Pregap != 0)
            {
                warnings.Add(
                    $"Track {track.Number:00} is first in session {track.Session} and reports a {track.Pregap}-sector pregap. Classic MDS stores its start offset at the first represented sector, so that session pregap is metadata only and is not prepended to the BIN.");
            }
        }

        bool hasSub = inspectedTracks.Any(t => t.HasInterleavedSubchannel);
        bool allSub = inspectedTracks.All(t => t.HasInterleavedSubchannel);
        long outputBytes = checked(outputCursor * CdMainChannelSize);

        return new Mdf2BinInspection(
            MdsPath: mdsPath,
            MdfPath: mdfPath,
            VersionMajor: versionMajor,
            VersionMinor: versionMinor,
            MediumType: mediumType,
            SessionCount: sessionCount,
            Tracks: inspectedTracks,
            OutputSectors: outputCursor,
            OutputBytes: outputBytes,
            HasInterleavedSubchannel: hasSub,
            AllTracksHaveInterleavedSubchannel: allSub,
            Warnings: warnings);
    }

    public async Task<Mdf2BinResult> ConvertAsync(
        string mdsPath,
        string? mdfOverridePath,
        string outputBinPath,
        string? outputCuePath = null,
        bool saveSubchannel = false,
        IProgress<Mdf2BinProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        Mdf2BinInspection inspection = await AnalyzeAsync(mdsPath, mdfOverridePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(outputBinPath))
            throw new ArgumentException("Choose an output BIN filename.", nameof(outputBinPath));

        outputBinPath = Path.GetFullPath(outputBinPath);
        outputCuePath = string.IsNullOrWhiteSpace(outputCuePath)
            ? Path.ChangeExtension(outputBinPath, ".cue")
            : Path.GetFullPath(outputCuePath);

        if (PathsEqual(outputBinPath, inspection.MdfPath) || PathsEqual(outputBinPath, inspection.MdsPath))
            throw new InvalidOperationException("Output BIN must not overwrite the source MDS/MDF image.");
        if (PathsEqual(outputCuePath, inspection.MdsPath) || PathsEqual(outputCuePath, inspection.MdfPath))
            throw new InvalidOperationException("Output CUE must not overwrite the source MDS/MDF image.");
        if (PathsEqual(outputBinPath, outputCuePath))
            throw new InvalidOperationException("Output BIN and CUE must use different filenames.");

        string? outputSubPath = saveSubchannel ? Path.ChangeExtension(outputBinPath, ".sub") : null;
        if (outputSubPath is not null && PathsEqual(outputSubPath, outputCuePath))
            throw new InvalidOperationException("Output CUE must not use the same filename as the optional .sub output.");
        if (saveSubchannel && !inspection.AllTracksHaveInterleavedSubchannel)
        {
            throw new NotSupportedException(
                "A .sub file was requested, but not every represented MDF sector has 96-byte interleaved subchannel data. DumpToolbox will not fabricate missing subchannel bytes; convert without .sub instead.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputBinPath) ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(outputCuePath) ?? Directory.GetCurrentDirectory());
        if (outputSubPath is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(outputSubPath) ?? Directory.GetCurrentDirectory());

        string binTemp = outputBinPath + ".partial";
        string cueTemp = outputCuePath + ".partial";
        string? subTemp = outputSubPath is null ? null : outputSubPath + ".partial";

        DeleteIfExists(binTemp);
        DeleteIfExists(cueTemp);
        if (subTemp is not null) DeleteIfExists(subTemp);

        long sectorsDone = 0;
        long inputBytesDone = 0;

        try
        {
            // Keep all MDF/BIN/SUB streams inside this scope so Windows has released the
            // temporary output handles before we rename .partial files into place below.
            {
                await using var mdf = new FileStream(
                    inspection.MdfPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var bin = new FileStream(
                    binTemp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream? sub = subTemp is null
                    ? null
                    : new FileStream(
                        subTemp,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        512 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                ulong? previousTrackPhysicalEnd = null;
                foreach (Mdf2BinTrackInspection track in inspection.Tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long trackSectors = checked((long)track.StoredPregapSectors + track.DataSectors);
                    bool isAudio = track.ModeCode is TrackAudio or TrackAudioAlt;
                    bool pregapOverlapsPrevious =
                        track.StoredPregapSectors > 0 &&
                        previousTrackPhysicalEnd.HasValue &&
                        track.PhysicalStartOffset < previousTrackPhysicalEnd.Value;
                    bool synthesizeAudioPregap = isAudio && pregapOverlapsPrevious;

                    activity?.Report(
                        $"Track {track.Number:00}: {track.Mode}; session {track.Session}; " +
                        $"{track.DataSectors:N0} data sector(s)" +
                        (track.StoredPregapSectors > 0 ? $" + {track.StoredPregapSectors:N0} stored pregap" : string.Empty) +
                        (track.HasInterleavedSubchannel ? "; stripping 96-byte interleaved subchannel" : string.Empty) + ".");

                    if (synthesizeAudioPregap)
                    {
                        activity?.Report(
                            $"Track {track.Number:00}: its {track.StoredPregapSectors:N0}-sector AUDIO pregap overlaps the preceding MDF track region; " +
                            "writing digital CDDA silence (2352 zero PCM bytes per sector) instead of copying the overlapping data-sector main channel.");
                    }

                    mdf.Position = checked((long)track.PhysicalStartOffset);
                    int recordSize = track.StoredSectorSize;
                    int blockSectors = Math.Max(1, (1024 * 1024) / recordSize);
                    byte[] inputBuffer = new byte[blockSectors * recordSize];
                    byte[]? mainBuffer = track.HasInterleavedSubchannel
                        ? new byte[blockSectors * CdMainChannelSize]
                        : null;
                    byte[]? subBuffer = sub is not null
                        ? new byte[blockSectors * CdSubchannelSize]
                        : null;
                    byte[]? silenceBuffer = synthesizeAudioPregap
                        ? new byte[blockSectors * CdMainChannelSize]
                        : null;

                    long remaining = trackSectors;
                    long pregapRemaining = track.StoredPregapSectors;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int takeSectors = (int)Math.Min(remaining, blockSectors);
                        int takeBytes = checked(takeSectors * recordSize);
                        await ReadExactlyAsync(mdf, inputBuffer.AsMemory(0, takeBytes), cancellationToken);

                        int silentPregapSectors = synthesizeAudioPregap
                            ? (int)Math.Min(pregapRemaining, takeSectors)
                            : 0;

                        if (!track.HasInterleavedSubchannel)
                        {
                            if (silentPregapSectors == 0)
                            {
                                await bin.WriteAsync(inputBuffer.AsMemory(0, checked(takeSectors * CdMainChannelSize)), cancellationToken);
                            }
                            else
                            {
                                await bin.WriteAsync(silenceBuffer!.AsMemory(0, checked(silentPregapSectors * CdMainChannelSize)), cancellationToken);
                                int normalSectors = takeSectors - silentPregapSectors;
                                if (normalSectors > 0)
                                {
                                    int sourceOffset = checked(silentPregapSectors * recordSize);
                                    await bin.WriteAsync(
                                        inputBuffer.AsMemory(sourceOffset, checked(normalSectors * CdMainChannelSize)),
                                        cancellationToken);
                                }
                            }
                        }
                        else
                        {
                            for (int i = 0; i < takeSectors; i++)
                            {
                                if (i < silentPregapSectors)
                                    Array.Clear(mainBuffer!, i * CdMainChannelSize, CdMainChannelSize);
                                else
                                    Buffer.BlockCopy(inputBuffer, i * recordSize, mainBuffer!, i * CdMainChannelSize, CdMainChannelSize);

                                if (subBuffer is not null)
                                {
                                    Buffer.BlockCopy(
                                        inputBuffer,
                                        i * recordSize + CdMainChannelSize,
                                        subBuffer,
                                        i * CdSubchannelSize,
                                        CdSubchannelSize);
                                }
                            }

                            await bin.WriteAsync(mainBuffer!.AsMemory(0, checked(takeSectors * CdMainChannelSize)), cancellationToken);
                            if (sub is not null)
                                await sub.WriteAsync(subBuffer!.AsMemory(0, checked(takeSectors * CdSubchannelSize)), cancellationToken);
                        }

                        remaining -= takeSectors;
                        pregapRemaining = Math.Max(0, pregapRemaining - takeSectors);
                        sectorsDone += takeSectors;
                        inputBytesDone += takeBytes;
                        progress?.Report(new Mdf2BinProgress(sectorsDone, inspection.OutputSectors, inputBytesDone));
                    }

                    previousTrackPhysicalEnd = checked(
                        track.PhysicalStartOffset +
                        (ulong)(track.StoredPregapSectors + track.DataSectors) * track.StoredSectorSize);
                }

                await bin.FlushAsync(cancellationToken);
                if (sub is not null)
                    await sub.FlushAsync(cancellationToken);
            }

            string cueText = BuildCue(inspection, Path.GetFileName(outputBinPath));
            await File.WriteAllTextAsync(cueTemp, cueText, new UTF8Encoding(false), cancellationToken);

            // All temporary output streams are disposed at this point. This is required
            // on Windows before File.Move/File.Replace can rename a file opened FileShare.None.
            ReplaceCompletedFile(binTemp, outputBinPath);
            ReplaceCompletedFile(cueTemp, outputCuePath);
            if (subTemp is not null && outputSubPath is not null)
                ReplaceCompletedFile(subTemp, outputSubPath);

            return new Mdf2BinResult(
                OutputBinPath: outputBinPath,
                OutputCuePath: outputCuePath,
                OutputSubPath: outputSubPath,
                SectorCount: inspection.OutputSectors,
                OutputBytes: inspection.OutputBytes,
                TrackCount: inspection.Tracks.Count,
                SessionCount: inspection.SessionCount);
        }
        catch
        {
            DeleteIfExists(binTemp);
            DeleteIfExists(cueTemp);
            if (subTemp is not null) DeleteIfExists(subTemp);
            throw;
        }
    }

    public static string SuggestMdfPath(string mdsPath) => Path.ChangeExtension(Path.GetFullPath(mdsPath), ".mdf");

    public static string SuggestBinPath(string mdsPath)
    {
        string full = Path.GetFullPath(mdsPath);
        return Path.Combine(
            Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory(),
            Path.GetFileNameWithoutExtension(full) + ".bin");
    }

    public static string SuggestCuePath(string outputBinPath) => Path.ChangeExtension(Path.GetFullPath(outputBinPath), ".cue");

    private static string BuildCue(Mdf2BinInspection inspection, string outputBinFileName)
    {
        var sb = new StringBuilder();
        sb.Append("FILE \"").Append(outputBinFileName.Replace("\"", "''", StringComparison.Ordinal)).AppendLine("\" BINARY");

        int previousSession = -1;
        foreach (Mdf2BinTrackInspection track in inspection.Tracks)
        {
            if (track.Session != previousSession)
            {
                sb.Append("  REM SESSION ").AppendLine(track.Session.ToString());
                previousSession = track.Session;
            }

            sb.Append("  TRACK ").Append(track.Number.ToString("00")).Append(' ').AppendLine(CueTrackType(track.ModeCode));

            if (track.StoredPregapSectors > 0)
                sb.Append("    INDEX 00 ").AppendLine(FormatCueTime(track.OutputIndex00Sector));

            sb.Append("    INDEX 01 ").AppendLine(FormatCueTime(track.OutputIndex01Sector));
        }

        return sb.ToString();
    }

    private static string ResolveMdfPath(
        string mdsPath,
        string? overridePath,
        Stream mdsStream,
        uint footerOffset,
        uint discMetadataOffset)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string defaultPath = SuggestMdfPath(mdsPath);
        if (footerOffset == 0 || (ulong)footerOffset + FooterSize > (ulong)mdsStream.Length)
            return defaultPath;

        long originalPosition = mdsStream.Position;
        try
        {
            mdsStream.Position = footerOffset;
            Span<byte> footer = stackalloc byte[FooterSize];
            int footerRead = 0;
            while (footerRead < FooterSize)
            {
                int n = mdsStream.Read(footer.Slice(footerRead));
                if (n == 0)
                    return defaultPath;
                footerRead += n;
            }

            uint filenameOffset = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(0, 4));
            uint widechar = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(4, 4));
            if (filenameOffset == 0 || filenameOffset >= mdsStream.Length)
                return defaultPath;

            long end = discMetadataOffset > filenameOffset && discMetadataOffset <= mdsStream.Length
                ? discMetadataOffset
                : mdsStream.Length;
            int length = checked((int)Math.Min(end - filenameOffset, 4096));
            if (length <= 0)
                return defaultPath;

            byte[] filenameBytes = new byte[length];
            mdsStream.Position = filenameOffset;
            int total = 0;
            while (total < filenameBytes.Length)
            {
                int n = mdsStream.Read(filenameBytes, total, filenameBytes.Length - total);
                if (n == 0) break;
                total += n;
            }

            string filename = widechar == 1
                ? DecodeNullTerminatedUnicode(filenameBytes.AsSpan(0, total))
                : DecodeNullTerminatedAnsi(filenameBytes.AsSpan(0, total));

            if (string.IsNullOrWhiteSpace(filename) || filename.Equals("*.mdf", StringComparison.OrdinalIgnoreCase))
                return defaultPath;

            string directory = Path.GetDirectoryName(mdsPath) ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(directory, filename));
        }
        finally
        {
            mdsStream.Position = originalPosition;
        }
    }

    private static string DecodeNullTerminatedUnicode(ReadOnlySpan<byte> data)
    {
        int length = data.Length - data.Length % 2;
        for (int i = 0; i + 1 < length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                length = i;
                break;
            }
        }

        return Encoding.Unicode.GetString(data.Slice(0, length)).Trim();
    }

    private static string DecodeNullTerminatedAnsi(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        if (nul >= 0) data = data.Slice(0, nul);
        return Encoding.Latin1.GetString(data).Trim();
    }

    private static bool IsSupportedCdTrackMode(byte mode) => mode is
        TrackAudio or TrackAudioAlt or
        TrackMode1 or TrackMode1Alt or
        TrackMode2 or TrackMode2F1 or TrackMode2F2 or TrackMode2F1Alt or TrackMode2F2Alt;

    private static string FormatTrackMode(byte mode) => mode switch
    {
        TrackAudio => "Audio",
        TrackAudioAlt => "Audio (alt)",
        TrackMode1 => "Mode 1",
        TrackMode1Alt => "Mode 1 (alt)",
        TrackMode2 => "Mode 2",
        TrackMode2F1 => "Mode 2 Form 1",
        TrackMode2F2 => "Mode 2 Form 2",
        TrackMode2F1Alt => "Mode 2 Form 1 (alt)",
        TrackMode2F2Alt => "Mode 2 Form 2 (alt)",
        TrackDvd => "DVD",
        _ => $"0x{mode:X2}"
    };

    private static string CueTrackType(byte mode) => mode switch
    {
        TrackAudio or TrackAudioAlt => "AUDIO",
        TrackMode1 or TrackMode1Alt => "MODE1/2352",
        TrackMode2 or TrackMode2F1 or TrackMode2F2 or TrackMode2F1Alt or TrackMode2F2Alt => "MODE2/2352",
        _ => throw new NotSupportedException($"Cannot map MDS track mode 0x{mode:X2} to CUE.")
    };

    private static string FormatCueTime(long sectors)
    {
        if (sectors < 0)
            throw new ArgumentOutOfRangeException(nameof(sectors));
        long minutes = sectors / (75 * 60);
        long remainder = sectors % (75 * 60);
        long seconds = remainder / 75;
        long frames = remainder % 75;
        return $"{minutes:00}:{seconds:00}:{frames:00}";
    }

    private static bool PathsEqual(string a, string b)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }

    private static void ReplaceCompletedFile(string tempPath, string finalPath)
    {
        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.Move(tempPath, finalPath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(total), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of file while reading MDS/MDF data.");
            total += read;
        }
    }

    private static Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        ReadExactlyAsync(stream, buffer.AsMemory(), cancellationToken);

    private sealed record MdsSession(
        int SessionStart,
        int SessionEnd,
        ushort Sequence,
        byte AllBlocks,
        byte NonTrackBlocks,
        ushort FirstTrack,
        ushort LastTrack,
        uint TrackOffset);

    private sealed record MdsTrack(
        int Session,
        ushort FirstTrackInSession,
        int Number,
        byte Mode,
        byte SubMode,
        byte AdrCtl,
        ushort SectorSize,
        uint StartLba,
        ulong StartOffset,
        uint Pregap,
        uint Sectors);
}
