using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

public enum CdiTrackKind
{
    Audio,
    Mode1Cooked,
    Mode1Raw,
    Mode2Cooked2048,
    Mode2Body2336,
    Mode2Raw
}

public sealed record CdiTrackInspection(
    int SessionNumber,
    int Number,
    CdiTrackKind Kind,
    int StoredSectorSize,
    uint ReadMode,
    long PregapSectors,
    long DataSectors,
    long SectorCount,
    long DiscIndex01Lba,
    long SourceOffset,
    long OutputIndex01Sector,
    bool HasFullSubchannel,
    bool HasPqSubchannel);

public sealed record Cdi2BinInspection(
    string FormatVersion,
    uint VersionValue,
    long DescriptorOffset,
    int SessionCount,
    IReadOnlyList<CdiTrackInspection> Tracks,
    long OutputSectors,
    long OutputBytes,
    bool HasFullSubchannel,
    bool HasPqSubchannel,
    bool IsDvd,
    IReadOnlyList<string> Warnings);

public sealed record Cdi2BinProgress(long InputBytesProcessed, long InputBytesTotal)
{
    public double Fraction => InputBytesTotal <= 0 ? 0 : Math.Clamp((double)InputBytesProcessed / InputBytesTotal, 0, 1);
}

public sealed record Cdi2BinResult(string OutputBinPath, string OutputCuePath, string? OutputSubPath, long SectorCount, long OutputBytes);

public sealed class Cdi2BinService
{
    private const uint CdiV2 = 0x80000004;
    private const uint CdiV3 = 0x80000005;
    private const uint CdiV35 = 0x80000006;
    private static readonly byte[] StartMark = { 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] Sync = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    private readonly record struct ParsedTrack(int Session, int Number, byte TrackMode, uint ReadMode, long Pregap, long Data, long Total, long StartLba, int StoredSize);

    public static string SuggestBinPath(string cdiPath) => Path.ChangeExtension(Path.GetFullPath(cdiPath), ".bin");
    public static string SuggestIsoPath(string cdiPath) => Path.ChangeExtension(Path.GetFullPath(cdiPath), ".iso");
    public static string SuggestCuePath(string binPath) => Path.ChangeExtension(Path.GetFullPath(binPath), ".cue");
    public static string SuggestSubPath(string binPath) => Path.ChangeExtension(Path.GetFullPath(binPath), ".sub");

    public async Task<Cdi2BinInspection> AnalyzeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) throw new InvalidOperationException("Choose a DiscJuggler CDI image.");
        string source = Path.GetFullPath(inputPath);
        if (!File.Exists(source)) throw new FileNotFoundException("CDI image was not found.", source);

        await using var metadataStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long imageLength = metadataStream.Length;
        if (imageLength < 16) throw new InvalidOperationException("The file is too small to be a DiscJuggler CDI image.");
        byte[] footer = new byte[8];
        metadataStream.Position = imageLength - 8;
        await ReadExactAsync(metadataStream, footer, cancellationToken);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(0, 4));
        uint footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4, 4));
        string versionText = version switch { CdiV2 => "2.0", CdiV3 => "3.0", CdiV35 => "3.5", _ => throw new InvalidOperationException($"Unsupported DiscJuggler CDI version 0x{version:X8}.") };
        if (footerOffset == 0) throw new InvalidOperationException("CDI descriptor offset/length is zero.");

        long descriptorOffset = version == CdiV35 ? imageLength - footerOffset : footerOffset;
        if (descriptorOffset <= 0 || descriptorOffset >= imageLength - 8)
            throw new InvalidOperationException($"CDI descriptor offset 0x{descriptorOffset:X} is outside the image.");
        long descriptorLength = imageLength - 8 - descriptorOffset;
        if (descriptorLength <= 0 || descriptorLength > 128L * 1024 * 1024)
            throw new InvalidOperationException($"CDI trailing descriptor length {descriptorLength:N0} bytes is unreasonable.");
        byte[] descriptor = new byte[checked((int)descriptorLength)];
        metadataStream.Position = descriptorOffset;
        await ReadExactAsync(metadataStream, descriptor, cancellationToken);

        var reader = new SpanReader(descriptor, 0, descriptor.Length);
        int sessions = reader.ReadUInt16();
        if (sessions <= 0 || sessions > 99) throw new InvalidOperationException($"Invalid CDI session count {sessions}.");

        var parsed = new List<ParsedTrack>();
        int fallbackTrack = 0;
        for (int session = 1; session <= sessions; session++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CDI 3.5 inserts a 13-byte inter-session block; older layouts use a
            // single separator byte.  Klax (USA) (Unl) is a confirmed 3.5 example.
            if (session > 1) reader.Skip(version == CdiV35 ? 13 : 1);
            int trackCount = reader.ReadUInt16();
            if (trackCount <= 0 || trackCount > 99) throw new InvalidOperationException($"Session {session} has invalid track count {trackCount}.");
            for (int i = 0; i < trackCount; i++)
            {
                fallbackTrack++;
                parsed.Add(ParseTrack(reader, session, fallbackTrack, version));
            }
        }

        if (parsed.Count == 0) throw new InvalidOperationException("The CDI descriptor contains no tracks.");
        parsed = ResolveSectorSizes(parsed, descriptorOffset);

        long sourceOffset = 0;
        long outputCursor = 0;
        var tracks = new List<CdiTrackInspection>(parsed.Count);
        var warnings = new List<string>();
        foreach (ParsedTrack p in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (p.Total <= 0) throw new InvalidOperationException($"Track {p.Number:00} has an invalid sector count {p.Total}.");
            CdiTrackKind kind = Classify(p.TrackMode, p.StoredSize);
            bool fullSub = p.StoredSize == 2448;
            bool pqSub = p.StoredSize == 2368;
            tracks.Add(new CdiTrackInspection(p.Session, p.Number, kind, p.StoredSize, p.ReadMode, p.Pregap, p.Data, p.Total, p.StartLba, sourceOffset, outputCursor + p.Pregap, fullSub, pqSub));
            sourceOffset = checked(sourceOffset + p.Total * p.StoredSize);
            outputCursor = checked(outputCursor + p.Total);
        }

        if (sourceOffset != descriptorOffset)
            throw new InvalidOperationException($"CDI track geometry consumes {sourceOffset:N0} bytes but the descriptor begins at {descriptorOffset:N0}; refusing to guess around a {descriptorOffset - sourceOffset:+#;-#;0}-byte discrepancy.");

        if (tracks.Any(t => t.HasPqSubchannel)) warnings.Add("One or more tracks store 2368-byte RAW-PQ sectors. Their 2352-byte main channel can be converted, but the 16-byte PQ-only subcode is not expanded into a 96-byte .sub file.");
        if (sessions > 1) warnings.Add("Standard CUE syntax cannot reproduce physical session lead-in/lead-out areas. Session numbers and original track LBAs will be retained as REM metadata; no lead-in/lead-out sectors are fabricated.");

        bool isDvd = sessions == 1 && tracks.Count == 1 && tracks[0].Kind == CdiTrackKind.Mode1Cooked &&
                     tracks[0].StoredSectorSize == 2048 && !tracks[0].HasFullSubchannel && !tracks[0].HasPqSubchannel &&
                     (tracks[0].DataSectors > 450000 || descriptorOffset > 900L * 1024 * 1024);
        if (isDvd) warnings.Add("DVD media inferred from a single native 2048-byte Mode 1 data track exceeding normal CD capacity. Output will be a 2048-byte ISO.");
        long finalSectors = isDvd ? tracks[0].DataSectors : outputCursor;
        long finalBytes = checked(finalSectors * (isDvd ? 2048L : 2352L));
        return new Cdi2BinInspection(versionText, version, descriptorOffset, sessions, tracks, finalSectors, finalBytes, tracks.Any(t => t.HasFullSubchannel), tracks.Any(t => t.HasPqSubchannel), isDvd, warnings);
    }

    public async Task<Cdi2BinResult> ConvertAsync(string inputPath, string outputBinPath, string outputCuePath, bool saveSubchannel, IProgress<Cdi2BinProgress>? progress = null, IProgress<string>? activity = null, CancellationToken cancellationToken = default)
    {
        Cdi2BinInspection inspection = await AnalyzeAsync(inputPath, cancellationToken);
        if (inspection.IsDvd)
        {
            string iso = Path.ChangeExtension(Path.GetFullPath(outputBinPath), ".iso");
            return await ConvertDvdIsoAsync(inputPath, iso, inspection, progress, activity, cancellationToken);
        }
        if (saveSubchannel && inspection.HasPqSubchannel)
            throw new InvalidOperationException("This CDI contains 2368-byte RAW-PQ sectors. Standard .sub files are 96 bytes per sector; DumpToolbox will not fabricate the missing R-W channels. Disable .sub output to convert the 2352-byte main channel.");

        string source = Path.GetFullPath(inputPath);
        string bin = Path.GetFullPath(outputBinPath);
        string cue = Path.GetFullPath(outputCuePath);
        string? sub = saveSubchannel && inspection.HasFullSubchannel ? SuggestSubPath(bin) : null;
        ValidateDifferent(source, bin, cue, sub);
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cue)!);
        if (sub is not null) Directory.CreateDirectory(Path.GetDirectoryName(sub)!);

        string binPartial = bin + ".partial";
        string cuePartial = cue + ".partial";
        string? subPartial = sub is null ? null : sub + ".partial";
        DeleteQuiet(binPartial); DeleteQuiet(cuePartial); if (subPartial is not null) DeleteQuiet(subPartial);

        byte[] inputSector = new byte[2448];
        byte[] raw = new byte[2352];
        byte[] zeros96 = new byte[96];
        long inputBytes = 0;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(binPartial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream? subOut = subPartial is null ? null : new FileStream(subPartial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

            foreach (CdiTrackInspection track in inspection.Tracks)
            {
                activity?.Report($"Session {track.SessionNumber:00} Track {track.Number:00}: {Describe(track.Kind)}, {track.SectorCount:N0} sector(s), stored {track.StoredSectorSize} bytes/sector.");
                long firstStoredLba = track.DiscIndex01Lba - track.PregapSectors;
                for (long i = 0; i < track.SectorCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReadExactAsync(input, inputSector.AsMemory(0, track.StoredSectorSize), cancellationToken);
                    long lba = firstStoredLba + i;
                    switch (track.Kind)
                    {
                        case CdiTrackKind.Mode1Cooked:
                            Iso2BinService.BuildRawSectorFromCooked(inputSector.AsSpan(0, 2048), raw, lba, CdSectorMode.Mode1);
                            break;
                        case CdiTrackKind.Mode2Cooked2048:
                            Iso2BinService.BuildRawSectorFromCooked(inputSector.AsSpan(0, 2048), raw, lba, CdSectorMode.Mode2Form1);
                            break;
                        case CdiTrackKind.Mode2Body2336:
                            BuildMode2From2336(inputSector.AsSpan(0, 2336), raw, lba);
                            break;
                        default:
                            Buffer.BlockCopy(inputSector, 0, raw, 0, 2352);
                            break;
                    }
                    await output.WriteAsync(raw, cancellationToken);
                    if (subOut is not null)
                    {
                        if (track.StoredSectorSize == 2448)
                            await subOut.WriteAsync(inputSector.AsMemory(2352, 96), cancellationToken);
                        else
                            await subOut.WriteAsync(zeros96, cancellationToken);
                    }
                    inputBytes += track.StoredSectorSize;
                    if ((i & 0x3FF) == 0) progress?.Report(new Cdi2BinProgress(inputBytes, inspection.DescriptorOffset));
                }
            }
            await output.FlushAsync(cancellationToken);
            if (subOut is not null) await subOut.FlushAsync(cancellationToken);

            // Windows will not allow the .partial files to be renamed while these
            // FileShare.None streams are still open. Dispose them before commit.
            if (subOut is not null) await subOut.DisposeAsync();
            await output.DisposeAsync();
            await input.DisposeAsync();

            string cueText = BuildCue(Path.GetFileName(bin), inspection);
            await File.WriteAllTextAsync(cuePartial, cueText, new UTF8Encoding(false), cancellationToken);
            Commit(binPartial, bin); Commit(cuePartial, cue); if (subPartial is not null && sub is not null) Commit(subPartial, sub);
            progress?.Report(new Cdi2BinProgress(inspection.DescriptorOffset, inspection.DescriptorOffset));
            return new Cdi2BinResult(bin, cue, sub, inspection.OutputSectors, inspection.OutputBytes);
        }
        catch
        {
            DeleteQuiet(binPartial); DeleteQuiet(cuePartial); if (subPartial is not null) DeleteQuiet(subPartial);
            throw;
        }
    }

    private static async Task<Cdi2BinResult> ConvertDvdIsoAsync(string inputPath, string outputIsoPath, Cdi2BinInspection inspection,
        IProgress<Cdi2BinProgress>? progress, IProgress<string>? activity, CancellationToken cancellationToken)
    {
        CdiTrackInspection track = inspection.Tracks[0];
        string source = Path.GetFullPath(inputPath);
        string iso = Path.GetFullPath(outputIsoPath);
        if (string.Equals(source, iso, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Output ISO must not overwrite the source CDI image.");
        Directory.CreateDirectory(Path.GetDirectoryName(iso)!);
        string partial = iso + ".partial"; DeleteQuiet(partial);
        long sourceStart = checked(track.SourceOffset + track.PregapSectors * 2048L);
        long remaining = checked(track.DataSectors * 2048L);
        long done = 0; byte[] buffer = new byte[1024 * 1024];
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = sourceStart; activity?.Report($"DVD detected: copying {track.DataSectors:N0} native 2048-byte sectors to ISO.");
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Unexpected end of CDI DVD payload.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read; done += read; progress?.Report(new Cdi2BinProgress(done, track.DataSectors * 2048L));
            }
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            await input.DisposeAsync();
            Commit(partial, iso);
            return new Cdi2BinResult(iso, string.Empty, null, track.DataSectors, track.DataSectors * 2048L);
        }
        catch { DeleteQuiet(partial); throw; }
    }

    private static ParsedTrack ParseTrack(SpanReader r, int session, int fallbackNumber, uint version)
    {
        uint temp = r.ReadUInt32();
        if (temp != 0) r.Skip(8); // DiscJuggler 4 extended data seen in later images.
        r.Require(StartMark); r.Require(StartMark);
        r.Skip(3); r.Skip(1);
        int fileNameLength = r.ReadByte();
        r.Skip(fileNameLength);
        r.Skip(11 + 1 + 10 + 1 + 4 + 2 + 2);
        int indexCount = r.ReadUInt16();
        if (indexCount < 0 || indexCount > 128) throw new InvalidOperationException($"CDI track has invalid index count {indexCount}.");
        uint idx0 = 0, idx1 = 0;
        for (int i = 0; i < indexCount; i++) { uint n = r.ReadUInt32(); if (i == 0) idx0 = n; else if (i == 1) idx1 = n; }
        uint cdTextCount = r.ReadUInt32();
        if (cdTextCount > 100000) throw new InvalidOperationException("CDI CD-Text block count is unreasonable.");
        r.Skip(checked((int)cdTextCount * 18));
        r.Skip(2);
        byte trackMode = r.ReadByte();
        if (trackMode > 2) throw new InvalidOperationException($"Unsupported CDI track mode {trackMode}.");
        r.Skip(7);
        uint storedSession = r.ReadUInt32();
        uint storedTrack = r.ReadUInt32();
        uint startLbaRaw = r.ReadUInt32();
        int length = r.ReadInt32();
        r.Skip(12 + 4);
        uint readMode = r.ReadUInt32();
        r.Skip(4 + 1);
        int duplicateTotal = r.ReadInt32();
        long pregap = indexCount >= 2 ? unchecked((int)idx0) : (long)duplicateTotal - length;
        long data = indexCount >= 2 ? unchecked((int)idx1) : length;
        long total = pregap + data;
        if (pregap < 0 || data < 0 || total <= 0) throw new InvalidOperationException("CDI track contains invalid negative/zero index lengths.");
        // The CDI 3.5 track trailer is 12 bytes shorter than the older
        // descriptor form.  Do not consume those 12 bytes or the next track's
        // start marker is skipped.
        r.Skip(4 + 12 + 4 + 1 + 8 + 4 + 4 + 4 + 4 + 4 + 42 + 4 + (version == CdiV35 ? 0 : 12));
        r.ReadUInt32(); r.Skip(2); r.ReadByte(); r.Skip(5);

        // DiscJuggler stores these ordinals zero-based and the track ordinal is
        // session-local.  CUE track numbers are disc-global, so retain our
        // sequential fallback number while converting the stored session to
        // its one-based display value.
        int number = fallbackNumber;
        int sessionNumber = storedSession <= 98 ? checked((int)storedSession + 1) : session;
        long startLba = unchecked((int)startLbaRaw);
        int size = readMode switch { 0 => 2048, 1 => 2336, 2 => 2352, _ => 0 };
        return new ParsedTrack(sessionNumber, number, trackMode, readMode, pregap, data, total, startLba, size);
    }

    private static List<ParsedTrack> ResolveSectorSizes(List<ParsedTrack> tracks, long payloadBytes)
    {
        long known = tracks.Where(t => t.StoredSize != 0).Sum(t => checked(t.Total * t.StoredSize));
        var unknownModes = tracks.Where(t => t.StoredSize == 0).Select(t => t.ReadMode).Distinct().ToArray();
        if (unknownModes.Length == 0) return tracks;
        if (unknownModes.Length > 8) throw new InvalidOperationException("CDI uses too many unknown read-mode values to resolve safely.");

        var solutions = new List<Dictionary<uint, int>>();
        int[] candidates = { 2368, 2448 };
        void Search(int index, Dictionary<uint, int> map)
        {
            if (solutions.Count > 1) return;
            if (index == unknownModes.Length)
            {
                long total = known;
                foreach (ParsedTrack t in tracks.Where(t => t.StoredSize == 0)) total = checked(total + t.Total * map[t.ReadMode]);
                if (total == payloadBytes) solutions.Add(new Dictionary<uint, int>(map));
                return;
            }
            foreach (int size in candidates) { map[unknownModes[index]] = size; Search(index + 1, map); }
            map.Remove(unknownModes[index]);
        }
        Search(0, new Dictionary<uint, int>());
        if (solutions.Count != 1)
        {
            string modes = string.Join(", ", unknownModes.Select(v => $"0x{v:X}"));
            throw new InvalidOperationException(solutions.Count == 0
                ? $"Unsupported CDI read-mode value(s) {modes}; 2368/2448 sector-size inference does not fit the descriptor offset."
                : $"Ambiguous CDI read-mode value(s) {modes}; both 2368/2448 interpretations fit, so conversion is refused rather than guessed.");
        }
        Dictionary<uint, int> solution = solutions[0];
        return tracks.Select(t => t.StoredSize == 0 ? t with { StoredSize = solution[t.ReadMode] } : t).ToList();
    }

    private static CdiTrackKind Classify(byte mode, int size) => (mode, size) switch
    {
        (0, 2352) or (0, 2368) or (0, 2448) => CdiTrackKind.Audio,
        (1, 2048) => CdiTrackKind.Mode1Cooked,
        (1, 2352) or (1, 2368) or (1, 2448) => CdiTrackKind.Mode1Raw,
        (2, 2048) => CdiTrackKind.Mode2Cooked2048,
        (2, 2336) => CdiTrackKind.Mode2Body2336,
        (2, 2352) or (2, 2368) or (2, 2448) => CdiTrackKind.Mode2Raw,
        _ => throw new InvalidOperationException($"Unsupported CDI track-mode/sector-size combination: mode {mode}, {size} bytes.")
    };

    private static void BuildMode2From2336(ReadOnlySpan<byte> body, Span<byte> raw, long lba)
    {
        raw.Clear(); Sync.CopyTo(raw); long frame = lba + 150;
        raw[12] = Bcd((int)(frame / 4500)); raw[13] = Bcd((int)((frame / 75) % 60)); raw[14] = Bcd((int)(frame % 75)); raw[15] = 0x02;
        body.CopyTo(raw.Slice(16, 2336));
    }
    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    private static string BuildCue(string binName, Cdi2BinInspection inspection)
    {
        var sb = new StringBuilder(); sb.AppendLine($"FILE \"{binName.Replace("\"", "''")}\" BINARY");
        int lastSession = -1;
        foreach (CdiTrackInspection t in inspection.Tracks)
        {
            if (t.SessionNumber != lastSession) { sb.AppendLine($"  REM SESSION {t.SessionNumber:00}"); lastSession = t.SessionNumber; }
            sb.AppendLine($"  REM ORIGINAL_LBA TRACK {t.Number:00} {t.DiscIndex01Lba}");
            sb.AppendLine($"  TRACK {t.Number:00} {(t.Kind == CdiTrackKind.Audio ? "AUDIO" : t.Kind is CdiTrackKind.Mode1Cooked or CdiTrackKind.Mode1Raw ? "MODE1/2352" : "MODE2/2352")}");
            if (t.PregapSectors > 0) sb.AppendLine($"    INDEX 00 {CueTime(t.OutputIndex01Sector - t.PregapSectors)}");
            sb.AppendLine($"    INDEX 01 {CueTime(t.OutputIndex01Sector)}");
        }
        return sb.ToString();
    }
    private static string CueTime(long sectors) => $"{sectors / 4500:00}:{(sectors / 75) % 60:00}:{sectors % 75:00}";
    private static string Describe(CdiTrackKind kind) => kind switch { CdiTrackKind.Audio => "Audio", CdiTrackKind.Mode1Cooked => "Mode 1 2048→2352", CdiTrackKind.Mode1Raw => "Mode 1 raw", CdiTrackKind.Mode2Cooked2048 => "Mode 2 2048→2352", CdiTrackKind.Mode2Body2336 => "Mode 2 2336→2352", _ => "Mode 2 raw" };

    private static async Task ReadExactAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    { int done = 0; while (done < buffer.Length) { int n = await input.ReadAsync(buffer.Slice(done), token); if (n == 0) throw new EndOfStreamException("Unexpected end of CDI track payload."); done += n; } }
    private static void ValidateDifferent(params string?[] paths) { var vals = paths.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray(); if (vals.Distinct(StringComparer.OrdinalIgnoreCase).Count() != vals.Length) throw new InvalidOperationException("Input and output filenames must be different."); }
    private static void Commit(string partial, string destination) { if (File.Exists(destination)) File.Delete(destination); File.Move(partial, destination); }
    private static void DeleteQuiet(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class SpanReader
    {
        private readonly byte[] _data; private readonly int _end; public int Position;
        public SpanReader(byte[] data, int start, int end) { _data = data; Position = start; _end = end; }
        private ReadOnlySpan<byte> Take(int n) { if (n < 0 || Position > _end - n) throw new InvalidOperationException("Truncated or malformed CDI descriptor."); var s = _data.AsSpan(Position, n); Position += n; return s; }
        public void Skip(int n) => Take(n);
        public byte ReadByte() => Take(1)[0];
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public void Require(ReadOnlySpan<byte> expected) { if (!Take(expected.Length).SequenceEqual(expected)) throw new InvalidOperationException("CDI track start marker was not found where expected; descriptor variant is unsupported or malformed."); }
    }
}
