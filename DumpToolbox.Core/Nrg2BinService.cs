using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

public enum NrgTrackKind
{
    Audio,
    Mode1Cooked,
    Mode1Raw,
    Mode2Cooked,
    Mode2Raw
}

public sealed record NrgChunkInspection(string Id, long Offset, int PayloadBytes);

public sealed record NrgTrackInspection(
    int SessionNumber,
    int Number,
    NrgTrackKind Kind,
    int StoredSectorSize,
    bool HasSubchannel,
    long SourceOffset,
    long SourceBytes,
    long SectorCount,
    long DiscIndex01Lba,
    long PregapSectors,
    long OutputIndex00Sector,
    long OutputIndex01Sector,
    long OutputEndSector);

public sealed record Nrg2BinInspection(
    string InputPath,
    int FormatVersion,
    string FooterId,
    long ChunkChainOffset,
    int SessionCount,
    string RecordingMode,
    IReadOnlyList<NrgTrackInspection> Tracks,
    IReadOnlyList<NrgChunkInspection> Chunks,
    long OutputSectors,
    long OutputBytes,
    bool HasSubchannel,
    bool IsDvd,
    uint? MediaTypeValue,
    IReadOnlyList<string> Warnings);

public sealed record Nrg2BinProgress(long SectorsProcessed, long TotalSectors, long InputBytesProcessed)
{
    public double Fraction => TotalSectors <= 0 ? 0 : Math.Clamp((double)SectorsProcessed / TotalSectors, 0, 1);
}

public sealed record Nrg2BinResult(
    string OutputBinPath,
    string OutputCuePath,
    string? OutputSubPath,
    long SectorCount,
    long OutputBytes,
    int TrackCount,
    int SessionCount);

/// <summary>
/// Converts Nero NRG CD images to a conventional 2352-byte BIN plus CUE.
/// 2448-byte NRG sectors are split losslessly into 2352-byte main-channel BIN data
/// and a companion 96-byte-per-sector SUB file. Multi-session NRG track payloads are
/// retained in session order; CUE REM lines preserve session boundaries/original LBAs
/// because conventional CUE syntax cannot encode physical session lead-in/lead-out areas.
/// </summary>
public sealed class Nrg2BinService
{
    private const int RawSectorSize = 2352;
    private const int SubchannelBytes = 96;
    private const int RawWithSubSectorSize = RawSectorSize + SubchannelBytes;
    private const int CookedSectorSize = 2048;
    private const int CopyBufferSectors = 256;

    public async Task<Nrg2BinInspection> AnalyzeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Choose an NRG image.", nameof(inputPath));

        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("NRG image not found.", inputPath);

        await using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

        (int version, string footerId, long chainOffset, long footerOffset) = await ReadFooterAsync(stream, cancellationToken);
        List<Chunk> chunks = await ReadChunksAsync(stream, chainOffset, footerOffset, cancellationToken);

        var warnings = new List<string>();
        if (chunks.Any(c => c.Id == "CDTX"))
            warnings.Add("CD-TEXT metadata is present. NRG2BIN preserves track data but does not currently emit CD-TEXT into the CUE.");

        List<Chunk> geometryChunks = chunks.Where(c => c.Id is "DAOI" or "DAOX" or "ETNF" or "ETN2").ToList();
        if (geometryChunks.Count == 0)
            throw new NotSupportedException("The NRG contains neither DAOI/DAOX nor ETNF/ETN2 track geometry.");

        List<int> declaredSessionTracks = chunks.Where(c => c.Id == "SINF").Select(ParseSinfTrackCount).ToList();
        int sessionCount = declaredSessionTracks.Count > 0 ? declaredSessionTracks.Count : geometryChunks.Count;
        if (sessionCount <= 0) sessionCount = 1;

        if (geometryChunks.Count != sessionCount)
        {
            if (sessionCount == 1)
                geometryChunks = new List<Chunk> { geometryChunks[0] };
            else
                throw new NotSupportedException($"NRG metadata describes {sessionCount} sessions but exposes {geometryChunks.Count} DAO/TAO geometry chunks; the session mapping is ambiguous.");
        }

        List<Chunk> cueChunks = chunks.Where(c => c.Id is "CUEX" or "CUES").ToList();
        var allTracks = new List<NrgTrackInspection>();
        long outputCursor = 0;
        int nextTaoTrackNumber = 1;
        var modeNames = new List<string>();

        for (int sessionIndex = 0; sessionIndex < geometryChunks.Count; sessionIndex++)
        {
            int sessionNumber = sessionIndex + 1;
            Chunk geometry = geometryChunks[sessionIndex];
            Dictionary<(int Track, int Index), int> cueLbas = cueChunks.Count switch
            {
                0 => new Dictionary<(int, int), int>(),
                1 => ParseCuePoints(new[] { cueChunks[0] }),
                _ when sessionIndex < cueChunks.Count => ParseCuePoints(new[] { cueChunks[sessionIndex] }),
                _ => new Dictionary<(int, int), int>()
            };

            List<NrgTrackInspection> sessionTracks;
            if (geometry.Id is "DAOX" or "DAOI")
            {
                modeNames.Add("DAO");
                sessionTracks = ParseDaoTracks(geometry, cueLbas, chainOffset, warnings, sessionNumber, outputCursor);
            }
            else
            {
                modeNames.Add("TAO");
                sessionTracks = ParseTaoTracks(geometry, chainOffset, warnings, sessionNumber, outputCursor, nextTaoTrackNumber);
            }

            if (sessionTracks.Count == 0)
                throw new InvalidDataException($"Session {sessionNumber} contains no supported CD tracks.");

            if (sessionIndex < declaredSessionTracks.Count && declaredSessionTracks[sessionIndex] > 0 &&
                declaredSessionTracks[sessionIndex] != sessionTracks.Count)
            {
                throw new InvalidDataException($"Session {sessionNumber} SINF declares {declaredSessionTracks[sessionIndex]} track(s), but {geometry.Id} contains {sessionTracks.Count}.");
            }

            allTracks.AddRange(sessionTracks);
            outputCursor = sessionTracks.Max(t => t.OutputEndSector);
            nextTaoTrackNumber = Math.Max(nextTaoTrackNumber, sessionTracks.Max(t => t.Number) + 1);
        }

        if (allTracks.Count == 0)
            throw new InvalidDataException("No supported CD tracks were found in the NRG metadata.");

        bool hasSubchannel = allTracks.Any(t => t.HasSubchannel);
        if (hasSubchannel)
        {
            warnings.Add("One or more tracks contain stored 96-byte subchannel data. A companion .sub file can optionally be written to preserve those bytes losslessly.");
            if (allTracks.Any(t => !t.HasSubchannel))
                warnings.Add("The NRG mixes subchannel and non-subchannel tracks. The .sub file will contain zero placeholders for sectors where the NRG stores no subchannel bytes, preserving one 96-byte SUB record per BIN sector.");
        }
        if (sessionCount > 1)
            warnings.Add("Multisession image: all stored session track payloads are retained. Standard CUE cannot encode physical session lead-in/lead-out areas, so session number and original disc LBA are preserved as REM metadata instead of synthesizing unknown sectors.");

        uint? mediaTypeValue = chunks.Where(c => c.Id == "MTYP").Select(ParseMtyp).FirstOrDefault(v => v.HasValue);
        bool isDvd = mediaTypeValue.HasValue && IsNrgDvdMediaType(mediaTypeValue.Value);
        if (!isDvd && allTracks.Count == 1 && allTracks[0].Kind == NrgTrackKind.Mode1Cooked &&
            allTracks[0].StoredSectorSize == CookedSectorSize && allTracks[0].SectorCount > 450000)
        {
            isDvd = true;
            warnings.Add("DVD classification was inferred from a single 2048-byte data track exceeding normal CD capacity because MTYP was absent or unrecognized.");
        }

        if (isDvd)
        {
            if (sessionCount != 1 || allTracks.Count != 1 || allTracks[0].Kind != NrgTrackKind.Mode1Cooked || allTracks[0].StoredSectorSize != CookedSectorSize)
                throw new NotSupportedException("This NRG is identified as DVD media, but its stored layout is not a single 2048-byte Mode 1 data track. Refusing to invent a DVD ISO layout.");
            if (allTracks[0].HasSubchannel)
                throw new NotSupportedException("DVD NRG unexpectedly declares CD subchannel data; refusing an ambiguous conversion.");
        }

        long outputSectors = isDvd ? allTracks[0].SectorCount - allTracks[0].PregapSectors : allTracks.Max(t => t.OutputEndSector);
        if (outputSectors <= 0)
            throw new InvalidDataException("The NRG track layout produces no output sectors.");

        string recordingMode = string.Join("/", modeNames.Distinct(StringComparer.OrdinalIgnoreCase));
        return new Nrg2BinInspection(
            inputPath,
            version,
            footerId,
            chainOffset,
            sessionCount,
            recordingMode,
            allTracks,
            chunks.Select(c => new NrgChunkInspection(c.Id, c.Offset, c.Payload.Length)).ToArray(),
            outputSectors,
            checked(outputSectors * (isDvd ? CookedSectorSize : RawSectorSize)),
            hasSubchannel,
            isDvd,
            mediaTypeValue,
            warnings);
    }

    public async Task<Nrg2BinResult> ConvertAsync(
        string inputPath,
        string outputBinPath,
        string? outputCuePath = null,
        bool saveSubchannel = false,
        IProgress<Nrg2BinProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        Nrg2BinInspection inspection = await AnalyzeAsync(inputPath, cancellationToken);
        outputBinPath = Path.GetFullPath(outputBinPath);
        if (inspection.IsDvd)
        {
            if (!string.Equals(Path.GetExtension(outputBinPath), ".iso", StringComparison.OrdinalIgnoreCase))
                outputBinPath = Path.ChangeExtension(outputBinPath, ".iso");
            return await ConvertDvdIsoAsync(inspection, outputBinPath, progress, activity, cancellationToken);
        }
        outputCuePath = string.IsNullOrWhiteSpace(outputCuePath) ? SuggestCuePath(outputBinPath) : Path.GetFullPath(outputCuePath);
        string? outputSubPath = saveSubchannel && inspection.HasSubchannel ? SuggestSubPath(outputBinPath) : null;

        if (PathsEqual(inspection.InputPath, outputBinPath) || PathsEqual(inspection.InputPath, outputCuePath) ||
            (outputSubPath is not null && PathsEqual(inspection.InputPath, outputSubPath)))
            throw new InvalidOperationException("Output files must not overwrite the source NRG image.");
        if (PathsEqual(outputBinPath, outputCuePath) || (outputSubPath is not null &&
            (PathsEqual(outputBinPath, outputSubPath) || PathsEqual(outputCuePath, outputSubPath))))
            throw new InvalidOperationException("Output BIN, CUE and SUB paths must be different.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputBinPath) ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(outputCuePath) ?? Directory.GetCurrentDirectory());
        if (outputSubPath is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(outputSubPath) ?? Directory.GetCurrentDirectory());

        string binTemp = outputBinPath + ".partial";
        string cueTemp = outputCuePath + ".partial";
        string? subTemp = outputSubPath is null ? null : outputSubPath + ".partial";
        TryDelete(binTemp);
        TryDelete(cueTemp);
        if (subTemp is not null) TryDelete(subTemp);

        long sectorsDone = 0;
        long inputBytesDone = 0;

        try
        {
            await using var input = new FileStream(inspection.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using var output = new FileStream(binTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream? subOutput = subTemp is null ? null : new FileStream(subTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] cooked = new byte[CookedSectorSize];
            byte[] raw = new byte[RawSectorSize];
            byte[] rawWithSub = new byte[RawWithSubSectorSize];
            byte[] emptySub = new byte[SubchannelBytes];
            byte[] copyBuffer = new byte[RawSectorSize * CopyBufferSectors];

            foreach (NrgTrackInspection track in inspection.Tracks.OrderBy(t => t.OutputIndex01Sector))
            {
                cancellationToken.ThrowIfCancellationRequested();
                activity?.Report($"Session {track.SessionNumber:00}, track {track.Number:00}: {DescribeKind(track.Kind, track.HasSubchannel)}, {track.SectorCount:N0} sectors at NRG offset 0x{track.SourceOffset:X}.");
                input.Position = track.SourceOffset;

                if (track.StoredSectorSize == RawWithSubSectorSize && track.HasSubchannel)
                {
                    for (long i = 0; i < track.SectorCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ReadExactlyAsync(input, rawWithSub, cancellationToken);
                        await output.WriteAsync(rawWithSub.AsMemory(0, RawSectorSize), cancellationToken);
                        if (subOutput is not null)
                            await subOutput.WriteAsync(rawWithSub.AsMemory(RawSectorSize, SubchannelBytes), cancellationToken);
                        inputBytesDone += RawWithSubSectorSize;
                        sectorsDone++;
                        if ((i & 0xFF) == 0 || i + 1 == track.SectorCount)
                            progress?.Report(new Nrg2BinProgress(sectorsDone, inspection.OutputSectors, inputBytesDone));
                    }
                }
                else if (track.StoredSectorSize == RawSectorSize && (track.Kind is NrgTrackKind.Audio or NrgTrackKind.Mode1Raw or NrgTrackKind.Mode2Raw))
                {
                    long remaining = track.SourceBytes;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int request = (int)Math.Min(copyBuffer.Length, remaining);
                        int read = await input.ReadAsync(copyBuffer.AsMemory(0, request), cancellationToken);
                        if (read == 0)
                            throw new EndOfStreamException($"Unexpected end of NRG data while reading track {track.Number:00}.");
                        if (read % RawSectorSize != 0)
                            throw new InvalidDataException($"Track {track.Number:00} returned a non-sector-aligned raw read.");
                        await output.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
                        long addedSectors = read / RawSectorSize;
                        if (subOutput is not null)
                            await WriteZeroSubSectorsAsync(subOutput, emptySub, addedSectors, cancellationToken);
                        remaining -= read;
                        inputBytesDone += read;
                        sectorsDone += addedSectors;
                        progress?.Report(new Nrg2BinProgress(sectorsDone, inspection.OutputSectors, inputBytesDone));
                    }
                }
                else if (track.StoredSectorSize == CookedSectorSize && (track.Kind is NrgTrackKind.Mode1Cooked or NrgTrackKind.Mode2Cooked))
                {
                    for (long i = 0; i < track.SectorCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ReadExactlyAsync(input, cooked, cancellationToken);
                        long lba = track.DiscIndex01Lba + (track.OutputIndex00Sector >= 0 ?
                            (track.OutputIndex00Sector + i - track.OutputIndex01Sector) : i);
                        Iso2BinService.BuildRawSectorFromCooked(
                            cooked,
                            raw,
                            lba,
                            track.Kind == NrgTrackKind.Mode1Cooked ? CdSectorMode.Mode1 : CdSectorMode.Mode2Form1);
                        await output.WriteAsync(raw, cancellationToken);
                        if (subOutput is not null)
                            await subOutput.WriteAsync(emptySub, cancellationToken);
                        inputBytesDone += CookedSectorSize;
                        sectorsDone++;
                        if ((i & 0xFF) == 0 || i + 1 == track.SectorCount)
                            progress?.Report(new Nrg2BinProgress(sectorsDone, inspection.OutputSectors, inputBytesDone));
                    }
                }
                else
                {
                    throw new NotSupportedException($"Track {track.Number:00} uses unsupported stored sector geometry ({track.StoredSectorSize} bytes, {track.Kind}).");
                }
            }

            await output.FlushAsync(cancellationToken);
            if (subOutput is not null) await subOutput.FlushAsync(cancellationToken);

            // FileShare.None keeps the temporary output locked on Windows until
            // disposal. Close all conversion streams before renaming them.
            if (subOutput is not null) await subOutput.DisposeAsync();
            await output.DisposeAsync();
            await input.DisposeAsync();

            string cue = BuildCue(inspection, Path.GetFileName(outputBinPath), outputSubPath is null ? null : Path.GetFileName(outputSubPath));
            await File.WriteAllTextAsync(cueTemp, cue, new UTF8Encoding(false), cancellationToken);

            ReplaceCompletedFile(binTemp, outputBinPath);
            ReplaceCompletedFile(cueTemp, outputCuePath);
            if (subTemp is not null && outputSubPath is not null)
                ReplaceCompletedFile(subTemp, outputSubPath);
            progress?.Report(new Nrg2BinProgress(inspection.OutputSectors, inspection.OutputSectors, inputBytesDone));

            return new Nrg2BinResult(outputBinPath, outputCuePath, outputSubPath, inspection.OutputSectors,
                inspection.OutputBytes, inspection.Tracks.Count, inspection.SessionCount);
        }
        catch
        {
            TryDelete(binTemp);
            TryDelete(cueTemp);
            if (subTemp is not null) TryDelete(subTemp);
            throw;
        }
    }

    public static string SuggestBinPath(string inputPath) => Path.ChangeExtension(Path.GetFullPath(inputPath), ".bin");
    public static string SuggestIsoPath(string inputPath) => Path.ChangeExtension(Path.GetFullPath(inputPath), ".iso");
    public static string SuggestCuePath(string outputBinPath) => Path.ChangeExtension(Path.GetFullPath(outputBinPath), ".cue");
    public static string SuggestSubPath(string outputBinPath) => Path.ChangeExtension(Path.GetFullPath(outputBinPath), ".sub");

    private static async Task<(int Version, string FooterId, long ChainOffset, long FooterOffset)> ReadFooterAsync(FileStream stream, CancellationToken token)
    {
        if (stream.Length < 8)
            throw new InvalidDataException("The file is too small to contain an NRG footer.");

        byte[] tail12 = new byte[(int)Math.Min(12L, stream.Length)];
        stream.Position = stream.Length - tail12.Length;
        await ReadExactlyAsync(stream, tail12, token);

        if (tail12.Length >= 12 && Encoding.ASCII.GetString(tail12, tail12.Length - 12, 4) == "NER5")
        {
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(tail12.AsSpan(tail12.Length - 8, 8));
            long footerOffset = stream.Length - 12;
            if (offset >= (ulong)footerOffset)
                throw new InvalidDataException("NER5 footer points outside the NRG chunk chain.");
            return (2, "NER5", checked((long)offset), footerOffset);
        }

        if (Encoding.ASCII.GetString(tail12, tail12.Length - 8, 4) == "NERO")
        {
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(tail12.AsSpan(tail12.Length - 4, 4));
            long footerOffset = stream.Length - 8;
            if (offset >= footerOffset)
                throw new InvalidDataException("NERO footer points outside the NRG chunk chain.");
            return (1, "NERO", offset, footerOffset);
        }

        throw new InvalidDataException("No Nero NRG footer was found (expected NER5 or NERO at the end of the file).");
    }

    private static async Task<List<Chunk>> ReadChunksAsync(FileStream stream, long chainOffset, long footerOffset, CancellationToken token)
    {
        var chunks = new List<Chunk>();
        stream.Position = chainOffset;
        byte[] header = new byte[8];
        while (stream.Position + 8 <= footerOffset)
        {
            token.ThrowIfCancellationRequested();
            long offset = stream.Position;
            await ReadExactlyAsync(stream, header, token);
            string id = Encoding.ASCII.GetString(header, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            if (size > int.MaxValue || stream.Position + size > footerOffset)
                throw new InvalidDataException($"NRG chunk {id} at 0x{offset:X} has an invalid payload length {size:N0}.");

            byte[] payload = new byte[size];
            if (size != 0)
                await ReadExactlyAsync(stream, payload, token);
            chunks.Add(new Chunk(id, offset, payload));
            if (id == "END!")
                break;
        }

        if (chunks.Count == 0)
            throw new InvalidDataException("The NRG metadata chunk chain is empty.");
        if (chunks.All(c => c.Id != "END!"))
            throw new InvalidDataException("The NRG metadata chunk chain has no END! marker.");
        return chunks;
    }

    private static int ParseSinfTrackCount(Chunk chunk)
    {
        if (chunk.Payload.Length < 4) return 0;
        return checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload.AsSpan(0, 4)));
    }

    private static Dictionary<(int Track, int Index), int> ParseCuePoints(IEnumerable<Chunk> chunks)
    {
        var result = new Dictionary<(int, int), int>();
        foreach (Chunk chunk in chunks.Where(c => c.Id is "CUEX" or "CUES"))
        {
            if (chunk.Payload.Length % 8 != 0)
                continue;
            for (int p = 0; p + 8 <= chunk.Payload.Length; p += 8)
            {
                int track = FromBcdOrBinary(chunk.Payload[p + 1]);
                int index = FromBcdOrBinary(chunk.Payload[p + 2]);
                if (track is < 1 or > 99 || index is < 0 or > 99)
                    continue;
                int lba = BinaryPrimitives.ReadInt32BigEndian(chunk.Payload.AsSpan(p + 4, 4));
                result[(track, index)] = lba;
            }
        }
        return result;
    }

    private static List<NrgTrackInspection> ParseDaoTracks(Chunk chunk, Dictionary<(int Track, int Index), int> cueLbas,
        long dataEndOffset, List<string> warnings, int sessionNumber, long outputStart)
    {
        bool v2 = chunk.Id == "DAOX";
        ReadOnlySpan<byte> p = chunk.Payload;
        int common = v2 ? 22 : 24;
        int recordSize = v2 ? 42 : 32;
        if (p.Length < common)
            throw new InvalidDataException($"{chunk.Id} chunk is too short.");

        int firstTrack = p[v2 ? 20 : 22];
        int lastTrack = p[v2 ? 21 : 23];
        int count = lastTrack >= firstTrack && firstTrack > 0 ? lastTrack - firstTrack + 1 : (p.Length - common) / recordSize;
        if (count <= 0 || common + count * recordSize > p.Length)
            throw new InvalidDataException($"{chunk.Id} contains an invalid DAO track table.");

        var temp = new List<ParsedTrack>(count);
        for (int i = 0; i < count; i++)
        {
            int off = common + i * recordSize;
            int number = firstTrack > 0 ? firstTrack + i : i + 1;
            int sectorSize;
            uint mode;
            long index0, index1, end;
            if (v2)
            {
                sectorSize = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(off + 12, 2));
                mode = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(off + 14, 2));
                index0 = checked((long)BinaryPrimitives.ReadUInt64BigEndian(p.Slice(off + 18, 8)));
                index1 = checked((long)BinaryPrimitives.ReadUInt64BigEndian(p.Slice(off + 26, 8)));
                end = checked((long)BinaryPrimitives.ReadUInt64BigEndian(p.Slice(off + 34, 8)));
            }
            else
            {
                sectorSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 12, 4)));
                mode = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 16, 4));
                index0 = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 20, 4));
                index1 = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 24, 4));
                end = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 28, 4));
            }

            bool hasSubchannel = sectorSize == RawWithSubSectorSize || IsSubchannelMode(mode);
            if (hasSubchannel && sectorSize != RawWithSubSectorSize)
                throw new InvalidDataException($"Track {number:00} declares a subchannel mode but stores {sectorSize}-byte sectors instead of 2448.");
            NrgTrackKind kind = DecodeKind(mode, sectorSize);
            long start = index0 >= 0 && index0 < index1 ? index0 : index1;
            ValidateTrackRange(number, start, end, sectorSize, dataEndOffset);
            long sourceBytes = end - start;
            long sectors = sourceBytes / sectorSize;
            long pregap = Math.Max(0, (index1 - start) / sectorSize);
            long discIndex1 = cueLbas.TryGetValue((number, 1), out int lba) ? lba : (number == firstTrack ? 0 : long.MinValue);
            if ((kind is NrgTrackKind.Mode1Cooked or NrgTrackKind.Mode2Cooked) && discIndex1 == long.MinValue)
                throw new NotSupportedException($"Track {number:00} is cooked data but the NRG has no usable CUEX/CUES INDEX 01 LBA needed to regenerate raw sector headers safely.");
            temp.Add(new ParsedTrack(number, kind, sectorSize, hasSubchannel, start, sourceBytes, sectors, discIndex1, pregap));
        }

        long cursor = outputStart;
        var result = new List<NrgTrackInspection>(temp.Count);
        foreach (ParsedTrack t in temp.OrderBy(t => t.Number))
        {
            long index00 = t.PregapSectors > 0 ? cursor : -1;
            long index01 = cursor + t.PregapSectors;
            long end = cursor + t.SectorCount;
            long discLba = t.DiscIndex01Lba == long.MinValue ? index01 : t.DiscIndex01Lba;
            result.Add(new NrgTrackInspection(sessionNumber, t.Number, t.Kind, t.SectorSize, t.HasSubchannel,
                t.SourceOffset, t.SourceBytes, t.SectorCount, discLba, t.PregapSectors, index00, index01, end));
            cursor = end;
        }
        return result;
    }

    private static List<NrgTrackInspection> ParseTaoTracks(Chunk chunk, long dataEndOffset, List<string> warnings,
        int sessionNumber, long outputStart, int firstTrackNumber)
    {
        bool v2 = chunk.Id == "ETN2";
        int recordSize = v2 ? 32 : 20;
        if (chunk.Payload.Length == 0 || chunk.Payload.Length % recordSize != 0)
            throw new InvalidDataException($"{chunk.Id} contains an invalid TAO track table length.");

        ReadOnlySpan<byte> p = chunk.Payload;
        var temp = new List<ParsedTrack>();
        int count = p.Length / recordSize;
        for (int i = 0; i < count; i++)
        {
            int off = i * recordSize;
            long sourceOffset, sourceBytes, sectors, startLba;
            uint mode;
            int sectorSize;
            if (v2)
            {
                sourceOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(p.Slice(off, 8)));
                sourceBytes = checked((long)BinaryPrimitives.ReadUInt64BigEndian(p.Slice(off + 8, 8)));
                mode = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 16, 4));
                startLba = BinaryPrimitives.ReadInt32BigEndian(p.Slice(off + 20, 4));
                sectors = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 28, 4));
                if (sectors <= 0 || sourceBytes % sectors != 0)
                    throw new InvalidDataException($"TAO track {firstTrackNumber + i:00} has inconsistent byte/sector counts.");
                sectorSize = checked((int)(sourceBytes / sectors));
            }
            else
            {
                sourceOffset = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off, 4));
                sourceBytes = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 4, 4));
                mode = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off + 8, 4));
                startLba = BinaryPrimitives.ReadInt32BigEndian(p.Slice(off + 12, 4));
                sectorSize = ExpectedStoredSectorSize(mode);
                if (sectorSize == 0 || sourceBytes % sectorSize != 0)
                    throw new NotSupportedException($"Legacy ETNF track {firstTrackNumber + i:00} has a sector layout that cannot be determined safely.");
                sectors = sourceBytes / sectorSize;
            }

            bool hasSubchannel = sectorSize == RawWithSubSectorSize || IsSubchannelMode(mode);
            if (hasSubchannel && sectorSize != RawWithSubSectorSize)
                throw new InvalidDataException($"Track {firstTrackNumber + i:00} declares a subchannel mode but stores {sectorSize}-byte sectors instead of 2448.");
            NrgTrackKind kind = DecodeKind(mode, sectorSize);
            ValidateTrackRange(firstTrackNumber + i, sourceOffset, sourceOffset + sourceBytes, sectorSize, dataEndOffset);
            temp.Add(new ParsedTrack(firstTrackNumber + i, kind, sectorSize, hasSubchannel, sourceOffset, sourceBytes, sectors, startLba, 0));
        }

        long cursor = outputStart;
        long? previousDiscEnd = null;
        var result = new List<NrgTrackInspection>();
        foreach (ParsedTrack t in temp)
        {
            long pregap = previousDiscEnd.HasValue ? Math.Max(0, t.DiscIndex01Lba - previousDiscEnd.Value) : Math.Max(0, t.DiscIndex01Lba);
            long index01 = cursor;
            long end = cursor + t.SectorCount;
            result.Add(new NrgTrackInspection(sessionNumber, t.Number, t.Kind, t.SectorSize, t.HasSubchannel,
                t.SourceOffset, t.SourceBytes, t.SectorCount, t.DiscIndex01Lba, pregap, -1, index01, end));
            cursor = end;
            previousDiscEnd = t.DiscIndex01Lba + t.SectorCount;
        }
        return result;
    }

    private static NrgTrackKind DecodeKind(uint mode, int sectorSize)
    {
        uint m = NormalizeMode(mode);
        if (m == 7 || m == 0x10)
        {
            if (sectorSize is not (RawSectorSize or RawWithSubSectorSize))
                throw new NotSupportedException($"Audio track uses unexpected {sectorSize}-byte sectors.");
            return NrgTrackKind.Audio;
        }
        if (m == 0)
            return sectorSize switch
            {
                CookedSectorSize => NrgTrackKind.Mode1Cooked,
                RawSectorSize or RawWithSubSectorSize => NrgTrackKind.Mode1Raw,
                _ => throw new NotSupportedException($"Mode 1 track uses unsupported {sectorSize}-byte sectors.")
            };
        if (m == 3)
            return sectorSize switch
            {
                CookedSectorSize => NrgTrackKind.Mode2Cooked,
                RawSectorSize or RawWithSubSectorSize => NrgTrackKind.Mode2Raw,
                _ => throw new NotSupportedException($"Mode 2 track uses unsupported {sectorSize}-byte sectors.")
            };
        if ((m == 5 || m == 0x0F) && (sectorSize == RawSectorSize || sectorSize == RawWithSubSectorSize)) return NrgTrackKind.Mode1Raw;
        if ((m == 6 || m == 0x11) && (sectorSize == RawSectorSize || sectorSize == RawWithSubSectorSize)) return NrgTrackKind.Mode2Raw;
        throw new NotSupportedException($"Unsupported Nero track mode 0x{mode:X} with {sectorSize}-byte sectors.");
    }

    private static uint NormalizeMode(uint mode)
    {
        if (mode <= 0xFF) return mode;
        if ((mode & 0xFF) == 0 && mode <= 0xFF00) return mode >> 8;
        return mode;
    }

    private static bool IsSubchannelMode(uint mode)
    {
        uint m = NormalizeMode(mode);
        return m is 0x0F or 0x10 or 0x11;
    }

    private static int ExpectedStoredSectorSize(uint mode)
    {
        uint m = NormalizeMode(mode);
        return m switch
        {
            0 or 3 => CookedSectorSize,
            5 or 6 or 7 => RawSectorSize,
            0x0F or 0x10 or 0x11 => RawWithSubSectorSize,
            _ => 0
        };
    }

    private static void ValidateTrackRange(int track, long start, long end, int sectorSize, long dataEndOffset)
    {
        if (sectorSize is not (CookedSectorSize or RawSectorSize or RawWithSubSectorSize))
            throw new NotSupportedException($"Track {track:00} uses unsupported {sectorSize}-byte sectors.");
        if (start < 0 || end <= start || end > dataEndOffset)
            throw new InvalidDataException($"Track {track:00} points outside the NRG data region.");
        if ((end - start) % sectorSize != 0)
            throw new InvalidDataException($"Track {track:00} byte length is not aligned to its {sectorSize}-byte sector size.");
    }

    private static uint? ParseMtyp(Chunk chunk)
    {
        if (chunk.Payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload.AsSpan(0, 4));
    }

    private static bool IsNrgDvdMediaType(uint value)
    {
        // Nero logs identify 0x06 as DVD-R/RW and 0x1C as the legacy generic DVD type.
        // Newer DVD media values also use the 0x40000 family (e.g. 0x4005E).
        return value is 0x00000006 or 0x0000001C || (value & 0x00040000) != 0;
    }

    private static async Task<Nrg2BinResult> ConvertDvdIsoAsync(Nrg2BinInspection inspection, string outputIsoPath,
        IProgress<Nrg2BinProgress>? progress, IProgress<string>? activity, CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(inspection.InputPath);
        string iso = Path.GetFullPath(outputIsoPath);
        if (PathsEqual(source, iso)) throw new InvalidOperationException("Output ISO must not overwrite the source NRG image.");
        Directory.CreateDirectory(Path.GetDirectoryName(iso) ?? Directory.GetCurrentDirectory());
        string temp = iso + ".partial";
        TryDelete(temp);
        NrgTrackInspection track = inspection.Tracks[0];
        long dataOffset = checked(track.SourceOffset + track.PregapSectors * CookedSectorSize);
        long bytesRemaining = checked(inspection.OutputSectors * CookedSectorSize);
        long bytesDone = 0;
        byte[] buffer = new byte[1024 * 1024];
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = dataOffset;
            activity?.Report($"DVD detected: copying {inspection.OutputSectors:N0} native 2048-byte sectors to ISO.");
            while (bytesRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, bytesRemaining);
                int read = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Unexpected end of NRG DVD payload.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesRemaining -= read; bytesDone += read;
                progress?.Report(new Nrg2BinProgress(bytesDone / CookedSectorSize, inspection.OutputSectors, bytesDone));
            }
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            await input.DisposeAsync();
            ReplaceCompletedFile(temp, iso);
            return new Nrg2BinResult(iso, string.Empty, null, inspection.OutputSectors, inspection.OutputBytes, 1, 1);
        }
        catch { TryDelete(temp); throw; }
    }

    private static string BuildCue(Nrg2BinInspection inspection, string outputBinFileName, string? outputSubFileName)
    {
        var sb = new StringBuilder();
        sb.Append("FILE \"").Append(outputBinFileName.Replace("\"", "\"\"", StringComparison.Ordinal)).AppendLine("\" BINARY");
        if (outputSubFileName is not null)
            sb.Append("REM SUBCHANNEL_FILE \"").Append(outputSubFileName.Replace("\"", "\"\"", StringComparison.Ordinal)).AppendLine("\"");

        int currentSession = -1;
        foreach (NrgTrackInspection track in inspection.Tracks.OrderBy(t => t.OutputIndex01Sector))
        {
            if (track.SessionNumber != currentSession)
            {
                currentSession = track.SessionNumber;
                sb.Append("REM SESSION ").AppendLine(currentSession.ToString("00"));
            }
            sb.Append("REM ORIGINAL_LBA TRACK ").Append(track.Number.ToString("00")).Append(' ').AppendLine(track.DiscIndex01Lba.ToString());
            if (track.HasSubchannel)
                sb.Append("REM TRACK ").Append(track.Number.ToString("00")).AppendLine(" HAS STORED 96-BYTE SUBCHANNEL");
            sb.Append("  TRACK ").Append(track.Number.ToString("00")).Append(' ').AppendLine(CueTrackType(track.Kind));
            if (track.OutputIndex00Sector < 0 && track.PregapSectors > 0)
                sb.Append("    PREGAP ").AppendLine(FormatCueTime(track.PregapSectors));
            if (track.OutputIndex00Sector >= 0 && track.OutputIndex00Sector < track.OutputIndex01Sector)
                sb.Append("    INDEX 00 ").AppendLine(FormatCueTime(track.OutputIndex00Sector));
            sb.Append("    INDEX 01 ").AppendLine(FormatCueTime(track.OutputIndex01Sector));
        }
        return sb.ToString();
    }

    private static string CueTrackType(NrgTrackKind kind) => kind switch
    {
        NrgTrackKind.Audio => "AUDIO",
        NrgTrackKind.Mode1Cooked or NrgTrackKind.Mode1Raw => "MODE1/2352",
        NrgTrackKind.Mode2Cooked or NrgTrackKind.Mode2Raw => "MODE2/2352",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string DescribeKind(NrgTrackKind kind, bool hasSubchannel)
    {
        string baseName = kind switch
        {
            NrgTrackKind.Audio => "audio/raw 2352",
            NrgTrackKind.Mode1Cooked => "Mode 1 cooked 2048 → raw 2352",
            NrgTrackKind.Mode1Raw => "Mode 1 raw 2352",
            NrgTrackKind.Mode2Cooked => "Mode 2 Form 1 cooked 2048 → raw 2352",
            NrgTrackKind.Mode2Raw => "Mode 2 raw 2352",
            _ => kind.ToString()
        };
        return hasSubchannel ? baseName + " + 96 sub" : baseName;
    }

    private static string FormatCueTime(long sectors)
    {
        if (sectors < 0) sectors = 0;
        long minutes = sectors / (75 * 60);
        long seconds = (sectors / 75) % 60;
        long frames = sectors % 75;
        return $"{minutes:00}:{seconds:00}:{frames:00}";
    }

    private static int FromBcdOrBinary(byte value)
    {
        int hi = value >> 4;
        int lo = value & 0x0F;
        return hi <= 9 && lo <= 9 ? hi * 10 + lo : value;
    }

    private static async Task WriteZeroSubSectorsAsync(Stream stream, byte[] zeroSector, long sectors, CancellationToken token)
    {
        for (long i = 0; i < sectors; i++)
            await stream.WriteAsync(zeroSector, token);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ReplaceCompletedFile(string temp, string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temp, destination);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record Chunk(string Id, long Offset, byte[] Payload);
    private sealed record ParsedTrack(int Number, NrgTrackKind Kind, int SectorSize, bool HasSubchannel, long SourceOffset,
        long SourceBytes, long SectorCount, long DiscIndex01Lba, long PregapSectors);
}
