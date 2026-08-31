using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public enum Iso2BinModeSelection
{
    Auto,
    Mode1,
    Mode2Form1
}

public enum CdSectorMode
{
    Mode1,
    Mode2Form1
}

public enum CueTrackKind
{
    Audio,
    Mode1_2048,
    Mode1_2352,
    Mode2_2048,
    Mode2_2352
}

public sealed record IsoInspectionResult(
    long InputBytes,
    long SectorCount,
    bool Is2048Aligned,
    bool Iso9660DescriptorFound,
    bool XaMarkerFound,
    CdSectorMode SuggestedMode,
    string DetectionMessage);

public sealed record CueTrackInspection(
    int Number,
    string SourceType,
    string OutputType,
    long StartFrame,
    long SectorCount,
    long SourceOffset,
    long SourceBytes,
    bool RequiresConversion,
    string SourceFilePath,
    string SourceFileType);

public sealed record CueInspectionResult(
    string CuePath,
    IReadOnlyList<string> SourceFiles,
    long InputBytes,
    long TotalSectors,
    IReadOnlyList<CueTrackInspection> Tracks,
    bool IsValid,
    string DetectionMessage);

public sealed record Iso2BinProgress(
    long SectorsProcessed,
    long TotalSectors,
    long InputBytesProcessed,
    long InputBytesTotal)
{
    public double Fraction => TotalSectors <= 0 ? 0 : (double)SectorsProcessed / TotalSectors;
}

public sealed record Iso2BinResult(
    string OutputPath,
    long SectorCount,
    long InputBytes,
    long OutputBytes,
    CdSectorMode Mode,
    bool ModeWasAutoDetected,
    string DetectionMessage,
    string? OutputCuePath = null,
    int TrackCount = 1);

public enum XaMetadataSourceKind
{
    DiscImageCreatorEccEdc,
    RedumperSkeleton
}

public sealed record XaMetadataInspection(
    string SourcePath,
    XaMetadataSourceKind SourceKind,
    long FirstLba,
    long LastLba,
    int SectorEntries,
    int Mode2Form1Entries,
    int Mode2Form2Entries,
    string DetectionMessage);

public sealed partial class Iso2BinService
{
    public const int CookedSectorSize = 2048;
    public const int RawSectorSize = 2352;

    private const int BatchSectors = 256;
    private const int MaxCdLba = 449_849;

    private static readonly byte[] SyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    private static readonly byte[] XaMarker = Encoding.ASCII.GetBytes("CD-XA001");
    private static readonly byte[] Cd001 = Encoding.ASCII.GetBytes("CD001");

    private static readonly byte[] EccForward = new byte[256];
    private static readonly byte[] EccBackward = new byte[256];
    private static readonly uint[] EdcTable = new uint[256];

    private static readonly Regex FileRegex = new(
        "^\\s*FILE\\s+(?:\"(?<name>[^\"]+)\"|(?<name>\\S+))\\s+(?<type>\\S+)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrackRegex = new(
        "^(?<indent>\\s*)TRACK\\s+(?<number>\\d+)\\s+(?<type>\\S+)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IndexRegex = new(
        "^\\s*INDEX\\s+(?<number>\\d+)\\s+(?<time>\\d+:\\d{2}:\\d{2})\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DicEccLbaRegex = new(@"LBA\[(?<lba>-?\d+),", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DicEccModeRegex = new(@"\bmode\s+(?<mode>[12])(?:\s+form\s+(?<form>[12]))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DicFileNumberRegex = new(@"FileNum\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex DicChannelNumberRegex = new(@"ChannelNum\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex DicSubmodeRegex = new(@"Submode\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex DicCodingInfoRegex = new(@"CodingInfo\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);

    static Iso2BinService()
    {
        for (int i = 0; i < 256; i++)
        {
            int j = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            EccForward[i] = (byte)j;
            EccBackward[i ^ j] = (byte)i;

            uint edc = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0u);
            EdcTable[i] = edc;
        }
    }

    public async Task<IsoInspectionResult> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Choose an input ISO file.", nameof(inputPath));

        string fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Input ISO file not found.", fullPath);

        long length = new FileInfo(fullPath).Length;
        bool aligned = length > 0 && length % CookedSectorSize == 0;
        long sectors = length / CookedSectorSize;

        bool descriptorFound = false;
        bool xaFound = false;

        if (aligned && sectors > 16)
        {
            byte[] descriptor = ArrayPool<byte>.Shared.Rent(CookedSectorSize);
            try
            {
                await using var input = OpenRead(fullPath, FileOptions.Asynchronous | FileOptions.RandomAccess, 64 * 1024);

                long lastDescriptorSector = Math.Min(sectors - 1, 63);
                for (long lba = 16; lba <= lastDescriptorSector; lba++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    input.Position = lba * CookedSectorSize;
                    await ReadExactlyAsync(input, descriptor.AsMemory(0, CookedSectorSize), cancellationToken);

                    if (!IsIso9660Descriptor(descriptor))
                        continue;

                    descriptorFound = true;
                    if (HasXaMarker(descriptor))
                        xaFound = true;

                    if (descriptor[0] == 0xFF)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(descriptor);
            }
        }

        CdSectorMode suggested = xaFound ? CdSectorMode.Mode2Form1 : CdSectorMode.Mode1;
        string message;
        if (!aligned)
        {
            message = length == 0
                ? "The input file is empty."
                : $"Input size {length:N0} is not an exact multiple of 2048 bytes.";
        }
        else if (xaFound)
        {
            message = "CD-XA001 marker found in an ISO9660 volume descriptor; Mode 2 Form 1 suggested.";
        }
        else if (descriptorFound)
        {
            message = "ISO9660 volume descriptor found with no CD-XA001 marker; Mode 1 suggested.";
        }
        else
        {
            message = "No ISO9660 CD-XA marker was found; Auto will use Mode 1. Select Mode 2 Form 1 manually if required.";
        }

        return new IsoInspectionResult(length, sectors, aligned, descriptorFound, xaFound, suggested, message);
    }

    public async Task<CueInspectionResult> InspectCueAsync(
        string cuePath,
        CancellationToken cancellationToken = default)
    {
        CueSheet sheet = await ParseCueAsync(cuePath, cancellationToken);
        CueLayout layout = await BuildCueLayoutAsync(sheet, cancellationToken);

        if (layout.TotalSectors <= 0)
            throw new InvalidOperationException("The CUE does not describe any sectors.");
        if (layout.TotalSectors - 1 > MaxCdLba)
            throw new InvalidOperationException($"The CUE describes {layout.TotalSectors:N0} sectors, exceeding the supported CD address range.");

        int cooked = layout.Tracks.Count(t => t.RequiresConversion);
        int audio = layout.Tracks.Count(t => t.SourceType.Equals("AUDIO", StringComparison.OrdinalIgnoreCase));
        int waveFiles = sheet.Files.Count(f => f.IsWave);
        int raw = layout.Tracks.Count - cooked - audio;
        string message =
            $"CUE validated: {layout.Tracks.Count:N0} tracks across {sheet.Files.Count:N0} file(s), {layout.TotalSectors:N0} sectors; " +
            $"{cooked:N0} cooked data track(s) to expand, {audio:N0} audio track(s) to copy" +
            (waveFiles > 0 ? $" ({waveFiles:N0} WAVE file(s), PCM data chunk only)" : string.Empty) +
            (raw > 0 ? $", {raw:N0} already-raw data track(s) to copy." : ".");

        return new CueInspectionResult(
            Path.GetFullPath(cuePath),
            sheet.Files.Select(f => f.FullPath).Distinct(StringComparerForPaths()).ToArray(),
            layout.InputBytes,
            layout.TotalSectors,
            layout.Tracks,
            true,
            message);
    }

    public async Task<XaMetadataInspection> InspectXaMetadataAsync(
        string metadataPath,
        CancellationToken cancellationToken = default)
    {
        XaMetadataMap map = await LoadXaMetadataAsync(metadataPath, cancellationToken);
        return map.Inspection;
    }

    public async Task<Iso2BinResult> ConvertAsync(
        string inputPath,
        string outputPath,
        Iso2BinModeSelection modeSelection,
        string? xaMetadataPath = null,
        HashTarget? target = null,
        IProgress<Iso2BinProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        IsoInspectionResult inspection = await InspectAsync(inputPath, cancellationToken);
        if (!inspection.Is2048Aligned)
            throw new InvalidOperationException(inspection.DetectionMessage);
        if (inspection.SectorCount == 0)
            throw new InvalidOperationException("The input ISO contains no 2048-byte sectors.");
        if (inspection.SectorCount - 1 > MaxCdLba)
            throw new InvalidOperationException(
                $"The input contains {inspection.SectorCount:N0} sectors, exceeding the address range of a CD-ROM raw sector header.");

        long outputSectorCount = inspection.SectorCount;
        if (target is not null)
        {
            if (target.Size <= 0 || target.Size % RawSectorSize != 0)
                throw new InvalidOperationException($"Redump target size {target.Size:N0} must be an exact multiple of 2352 bytes.");

            outputSectorCount = target.Size / RawSectorSize;
            if (outputSectorCount <= 0 || outputSectorCount - 1 > MaxCdLba)
                throw new InvalidOperationException($"Redump target describes {outputSectorCount:N0} sectors, outside the supported CD address range.");
        }

        string source = Path.GetFullPath(inputPath);
        string destination = Path.GetFullPath(outputPath);
        ValidateDifferentPaths(source, destination);

        XaMetadataMap? xaMetadata = null;
        if (!string.IsNullOrWhiteSpace(xaMetadataPath))
        {
            string metadataSource = Path.GetFullPath(xaMetadataPath);
            ValidateDifferentPaths(metadataSource, destination);
            xaMetadata = await LoadXaMetadataAsync(metadataSource, cancellationToken);
            activity?.Report(xaMetadata.Inspection.DetectionMessage);
        }

        CdSectorMode mode = modeSelection switch
        {
            Iso2BinModeSelection.Mode1 => CdSectorMode.Mode1,
            Iso2BinModeSelection.Mode2Form1 => CdSectorMode.Mode2Form1,
            _ when xaMetadata is not null && xaMetadata.Inspection.Mode2Form1Entries > 0 => CdSectorMode.Mode2Form1,
            _ => inspection.SuggestedMode
        };

        bool auto = modeSelection == Iso2BinModeSelection.Auto;
        activity?.Report(inspection.DetectionMessage);
        if (target is not null)
        {
            long delta = outputSectorCount - inspection.SectorCount;
            string? targetOutputFileName = target.OutputFileName;
            string targetName = string.IsNullOrWhiteSpace(targetOutputFileName) ? (target.Label ?? "Redump target") : targetOutputFileName;
            activity?.Report($"Redump target: {targetName}; {target.Size:N0} bytes = {outputSectorCount:N0} raw sectors; CRC32 {target.Crc32Hex}" +
                (target.NormalizedMd5 is null ? string.Empty : $"; MD5 {target.NormalizedMd5}") +
                (target.NormalizedSha1 is null ? string.Empty : $"; SHA-1 {target.NormalizedSha1}"));
            if (delta > 0)
                activity?.Report($"Input is short by {delta:N0} cooked sector(s); {delta * CookedSectorSize:N0} zero byte(s) will be appended before raw-sector generation.");
            else if (delta < 0)
                activity?.Report($"Input is long by {-delta:N0} cooked sector(s); the final {-delta * CookedSectorSize:N0} cooked byte(s) will be ignored so the raw output matches the Redump target length. The source ISO is not modified.");
            else
                activity?.Report("Input sector count already matches the Redump target length.");
        }
        if (auto && xaMetadata is not null && inspection.SuggestedMode == CdSectorMode.Mode1 && mode == CdSectorMode.Mode2Form1)
            activity?.Report("Auto mode selected Mode 2 Form 1 because the supplied XA metadata contains Mode 2 Form 1 sector records.");
        activity?.Report($"Output sector mode: {FormatMode(mode)}{(auto ? " (Auto)" : " (manual)")}.");
        if (mode == CdSectorMode.Mode2Form1)
        {
            activity?.Report(xaMetadata is null
                ? "Mode 2 Form 1 sectors use generic XA data subheader 00 00 08 00 (duplicated)."
                : "Mode 2 Form 1 sectors will use exact XA subheaders from the supplied metadata source where available; unmatched sectors fall back to 00 00 08 00.");
        }

        EnsureDestinationDirectory(destination);
        string partial = destination + ".partial";
        PreparePartial(source, partial);

        byte[] inputBuffer = ArrayPool<byte>.Shared.Rent(CookedSectorSize * BatchSectors);
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(RawSectorSize * BatchSectors);
        long processed = 0;

        var xaUsage = new XaMetadataUsage();

        try
        {
            await using (var input = OpenRead(source, FileOptions.Asynchronous | FileOptions.SequentialScan, CookedSectorSize * BatchSectors))
            await using (var output = OpenNewOutput(partial, RawSectorSize * BatchSectors))
            {
                while (processed < outputSectorCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sectorCount = (int)Math.Min(BatchSectors, outputSectorCount - processed);
                    int availableSectors = processed >= inspection.SectorCount
                        ? 0
                        : (int)Math.Min(sectorCount, inspection.SectorCount - processed);
                    int availableBytes = availableSectors * CookedSectorSize;
                    if (availableBytes > 0)
                        await ReadExactlyAsync(input, inputBuffer.AsMemory(0, availableBytes), cancellationToken);
                    if (availableSectors < sectorCount)
                        inputBuffer.AsSpan(availableBytes, (sectorCount - availableSectors) * CookedSectorSize).Clear();

                    BuildBatch(inputBuffer, outputBuffer, sectorCount, processed, mode, xaMetadata, xaUsage);

                    int outputBytes = sectorCount * RawSectorSize;
                    await output.WriteAsync(outputBuffer.AsMemory(0, outputBytes), cancellationToken);
                    processed += sectorCount;

                    progress?.Report(new Iso2BinProgress(
                        processed,
                        outputSectorCount,
                        processed * CookedSectorSize,
                        outputSectorCount * CookedSectorSize));
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);

            ReportXaUsage(activity, xaMetadata, xaUsage);

            long outputBytesTotal = checked(outputSectorCount * RawSectorSize);
            return new Iso2BinResult(
                destination,
                outputSectorCount,
                inspection.InputBytes,
                outputBytesTotal,
                mode,
                auto,
                inspection.DetectionMessage);
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

    public async Task<Iso2BinResult> ConvertCueAsync(
        string cuePath,
        string outputPath,
        string? xaMetadataPath = null,
        IProgress<Iso2BinProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        CueSheet sheet = await ParseCueAsync(cuePath, cancellationToken);
        CueLayout layout = await BuildCueLayoutAsync(sheet, cancellationToken);
        long totalSectors = layout.TotalSectors;
        if (totalSectors <= 0)
            throw new InvalidOperationException("The CUE does not describe any sectors.");
        if (totalSectors - 1 > MaxCdLba)
            throw new InvalidOperationException($"The CUE describes {totalSectors:N0} sectors, exceeding the supported CD address range.");

        string destination = Path.GetFullPath(outputPath);
        string outputCue = Path.ChangeExtension(destination, ".cue");
        string inputCue = Path.GetFullPath(cuePath);

        foreach (CueFileEntry sourceFile in sheet.Files)
        {
            ValidateDifferentPaths(sourceFile.FullPath, destination);
            ValidateDifferentPaths(sourceFile.FullPath, outputCue);
        }
        ValidateDifferentPaths(inputCue, destination);
        ValidateDifferentPaths(inputCue, outputCue);

        XaMetadataMap? xaMetadata = null;
        if (!string.IsNullOrWhiteSpace(xaMetadataPath))
        {
            string metadataSource = Path.GetFullPath(xaMetadataPath);
            ValidateDifferentPaths(metadataSource, destination);
            ValidateDifferentPaths(metadataSource, outputCue);
            xaMetadata = await LoadXaMetadataAsync(metadataSource, cancellationToken);
            activity?.Report(xaMetadata.Inspection.DetectionMessage);
        }

        EnsureDestinationDirectory(destination);
        string partial = destination + ".partial";
        string cuePartial = outputCue + ".partial";
        foreach (CueFileEntry sourceFile in sheet.Files)
            ValidateDifferentPaths(sourceFile.FullPath, partial);
        DeleteQuietly(partial);
        DeleteQuietly(cuePartial);

        byte[] cookedBuffer = ArrayPool<byte>.Shared.Rent(CookedSectorSize * BatchSectors);
        byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(RawSectorSize * BatchSectors);
        long sectorsProcessed = 0;
        long inputBytesProcessed = 0;

        var xaUsage = new XaMetadataUsage();

        try
        {
            activity?.Report($"CUE: {inputCue}");
            activity?.Report($"Source files: {sheet.Files.Count:N0}; tracks: {layout.Tracks.Count:N0}; total frames: {totalSectors:N0}.");
            foreach (CueFileEntry file in sheet.Files)
            {
                string description = file.IsWave
                    ? $"WAVE PCM payload {file.PayloadLength:N0} bytes at offset {file.PayloadOffset:N0}"
                    : $"{file.OriginalType} {file.PayloadLength:N0} bytes";
                activity?.Report($"Source: {file.FullPath} — {description}.");
            }

            await using (var output = OpenNewOutput(partial, RawSectorSize * BatchSectors))
            {
                for (int trackIndex = 0; trackIndex < layout.Tracks.Count; trackIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CueTrackInspection track = layout.Tracks[trackIndex];
                    CueTrack model = sheet.Tracks[trackIndex];
                    int sourceSectorSize = SectorSize(model.Kind);

                    activity?.Report(
                        $"Track {track.Number:00}: {Path.GetFileName(track.SourceFilePath)} [{track.SourceFileType}] " +
                        $"{track.SourceType} -> {track.OutputType}; {track.SectorCount:N0} sectors " +
                        $"({track.SourceBytes:N0} input bytes)." +
                        (track.RequiresConversion ? " Converting." : model.File.IsWave ? " Stripping WAVE container; copying PCM." : " Copying unchanged."));

                    await using var input = OpenRead(
                        track.SourceFilePath,
                        FileOptions.Asynchronous | FileOptions.SequentialScan,
                        RawSectorSize * BatchSectors);
                    input.Position = track.SourceOffset;

                    long trackProcessed = 0;
                    while (trackProcessed < track.SectorCount)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(BatchSectors, track.SectorCount - trackProcessed);

                        if (sourceSectorSize == CookedSectorSize)
                        {
                            int bytes = count * CookedSectorSize;
                            await ReadExactlyAsync(input, cookedBuffer.AsMemory(0, bytes), cancellationToken);

                            CdSectorMode mode = model.Kind == CueTrackKind.Mode2_2048
                                ? CdSectorMode.Mode2Form1
                                : CdSectorMode.Mode1;

                            BuildBatch(
                                cookedBuffer,
                                rawBuffer,
                                count,
                                sectorsProcessed,
                                mode,
                                mode == CdSectorMode.Mode2Form1 ? xaMetadata : null,
                                xaUsage);
                            await output.WriteAsync(rawBuffer.AsMemory(0, count * RawSectorSize), cancellationToken);
                            inputBytesProcessed += bytes;
                        }
                        else
                        {
                            int bytes = count * RawSectorSize;
                            await ReadExactlyAsync(input, rawBuffer.AsMemory(0, bytes), cancellationToken);
                            await output.WriteAsync(rawBuffer.AsMemory(0, bytes), cancellationToken);
                            inputBytesProcessed += bytes;
                        }

                        trackProcessed += count;
                        sectorsProcessed += count;
                        progress?.Report(new Iso2BinProgress(
                            sectorsProcessed,
                            totalSectors,
                            inputBytesProcessed,
                            layout.InputBytes));
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            if (inputBytesProcessed != layout.InputBytes)
                throw new InvalidOperationException(
                    $"CUE layout consumed {inputBytesProcessed:N0} source payload bytes but expected {layout.InputBytes:N0} bytes.");

            string cueText = GenerateOutputCue(sheet, Path.GetFileName(destination));
            await File.WriteAllTextAsync(cuePartial, cueText, new UTF8Encoding(false), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            File.Move(cuePartial, outputCue, overwrite: true);

            ReportXaUsage(activity, xaMetadata, xaUsage);

            long outputBytes = checked(totalSectors * RawSectorSize);
            activity?.Report($"Generated single-file CUE: {outputCue}");
            return new Iso2BinResult(
                destination,
                totalSectors,
                layout.InputBytes,
                outputBytes,
                CdSectorMode.Mode1,
                false,
                "CUE-defined mixed-mode conversion.",
                outputCue,
                layout.Tracks.Count);
        }
        catch
        {
            DeleteQuietly(partial);
            DeleteQuietly(cuePartial);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cookedBuffer);
            ArrayPool<byte>.Shared.Return(rawBuffer);
        }
    }

    public static string FormatMode(CdSectorMode mode) => mode switch
    {
        CdSectorMode.Mode2Form1 => "Mode 2 Form 1",
        _ => "Mode 1"
    };


    private readonly record struct XaSectorMetadata(
        int Mode,
        int Form,
        byte FileNumber,
        byte ChannelNumber,
        byte Submode,
        byte CodingInfo)
    {
        public static XaSectorMetadata GenericForm1 => new(2, 1, 0x00, 0x00, 0x08, 0x00);
    }

    private sealed class XaMetadataMap
    {
        private readonly IReadOnlyDictionary<long, XaSectorMetadata> _sectors;

        public XaMetadataMap(IReadOnlyDictionary<long, XaSectorMetadata> sectors, XaMetadataInspection inspection)
        {
            _sectors = sectors;
            Inspection = inspection;
        }

        public XaMetadataInspection Inspection { get; }

        public bool TryGet(long lba, out XaSectorMetadata metadata) => _sectors.TryGetValue(lba, out metadata);
    }

    private sealed class XaMetadataUsage
    {
        public long ExactSubheaders { get; set; }
        public long GenericSubheaders { get; set; }
    }

    private sealed record CueIndex(int Number, int Frame, string OriginalTime, int LineIndex);

    private sealed record WaveFormat(bool Pcm, ushort Channels, uint SampleRate, ushort BlockAlign, ushort BitsPerSample);
    private sealed record WaveInfo(long DataOffset, long DataLength);
    private sealed record CueLayout(IReadOnlyList<CueTrackInspection> Tracks, long InputBytes, long TotalSectors);

    private sealed class CueFileEntry
    {
        public CueFileEntry(string originalName, string originalType, int fileLineIndex)
        {
            OriginalName = originalName;
            OriginalType = originalType;
            FileLineIndex = fileLineIndex;
        }

        public string OriginalName { get; }
        public string OriginalType { get; }
        public int FileLineIndex { get; }
        public List<CueTrack> Tracks { get; } = new();
        public string FullPath { get; set; } = string.Empty;
        public bool IsWave { get; set; }
        public long PayloadOffset { get; set; }
        public long PayloadLength { get; set; }
        public long OutputStartFrame { get; set; }
    }

    private sealed class CueTrack
    {
        public CueTrack(int number, string originalType, CueTrackKind kind, int trackLineIndex, CueFileEntry file)
        {
            Number = number;
            OriginalType = originalType;
            Kind = kind;
            TrackLineIndex = trackLineIndex;
            File = file;
        }

        public int Number { get; }
        public string OriginalType { get; }
        public CueTrackKind Kind { get; }
        public int TrackLineIndex { get; }
        public CueFileEntry File { get; }
        public List<CueIndex> Indexes { get; } = new();
        public int EarliestFrame => Indexes.Min(i => i.Frame);
    }

    private sealed record CueSheet(
        string CuePath,
        string[] OriginalLines,
        List<CueFileEntry> Files,
        List<CueTrack> Tracks);

}
