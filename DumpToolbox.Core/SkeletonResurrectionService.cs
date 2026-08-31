using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public enum SkeletonImageKind
{
    Cooked2048,
    Raw2352
}

public enum SkeletonSpecialKind
{
    None,
    SystemArea,
    Gap,
    UnmappedHashEntry
}

public enum SkeletonSourceKind
{
    Redumper,
    DiscImageCreator
}

public sealed record SkeletonContentEntry(
    string Path,
    uint ExtentLba,
    long DataLength,
    string? Sha1,
    string? XaSha1,
    SkeletonSpecialKind SpecialKind = SkeletonSpecialKind.None,
    bool CanRestore = true,
    bool RequiresSource = false,
    long PhysicalSectorCount = 0,
    IReadOnlyList<string>? PathAliases = null,
    DateTimeOffset? RecordingTime = null,
    bool ContainsMode2Form2 = false,
    IReadOnlyList<SkeletonExtentSegment>? Extents = null,
    string? IsoOriginalPath = null,
    uint? IsoRecordExtentLba = null,
    byte IsoFileFlags = 0,
    IReadOnlyList<SkeletonAlternateIsoRecord>? AlternateIsoRecords = null)
{
    public bool IsSpecial => SpecialKind != SkeletonSpecialKind.None;
    public bool IsEmpty =>
        string.Equals(Sha1, SkeletonResurrectionService.EmptySha1, StringComparison.OrdinalIgnoreCase) ||
        (DataLength == 0 && SpecialKind == SkeletonSpecialKind.None);
}

public sealed record SkeletonInspectionResult(
    string SkeletonPath,
    string HashPath,
    SkeletonImageKind ImageKind,
    int SectorSize,
    int BaseLba,
    long SectorCount,
    IReadOnlyList<SkeletonContentEntry> Entries,
    string VolumeIdentifier,
    int HashEntryCount,
    int UnmappedHashEntryCount,
    SkeletonSourceKind SourceKind = SkeletonSourceKind.Redumper,
    string? ExpectedImageCrc32 = null,
    string? ExpectedImageMd5 = null,
    string? ExpectedImageSha1 = null,
    IReadOnlyList<string>? CompanionPaths = null,
    IReadOnlySet<long>? NoEdcLbas = null,
    IReadOnlySet<long>? DicMode2Form1QFaultLbas = null,
    IReadOnlySet<long>? DicFill55ExceptHeaderLbas = null,
    IReadOnlySet<long>? DicExactZeroSectorLbas = null,
    IReadOnlyDictionary<long, byte[]>? DicRawHeaderOverrides = null,
    IReadOnlyDictionary<long, byte[]>? DicXaSubheaderOverrides = null,
    IReadOnlyList<SkeletonDonorRequirement>? DonorRequirements = null,
    IReadOnlyDictionary<long, byte[]>? DicExactRawSectorOverrides = null,
    IReadOnlySet<long>? DicUnresolvedEccEdcMismatchLbas = null,
    IReadOnlySet<long>? DicExactMainInfoLbas = null,
    IReadOnlyList<DicSupplementaryDirectoryHint>? DicSupplementaryDirectoryHints = null,
    IReadOnlyList<DicHfsPartitionInspection>? DicHfsPartitions = null);

public sealed record DicHfsPartitionInspection(
    string Name,
    string Type,
    uint StartBlock,
    uint BlockCount,
    long PartitionStartLba,
    int PartitionStartByteOffset,
    long MasterDirectoryBlockLba,
    int MasterDirectoryBlockByteOffset,
    long VolumeBitmapStartLba,
    int VolumeBitmapStartByteOffset,
    bool MasterDirectoryBlockPresentInDicEvidence,
    DicHfsMasterDirectoryBlock? MasterDirectoryBlock,
    bool Phase1Synthesized = false,
    DicHfsMasterDirectoryBlock? SynthesizedMasterDirectoryBlock = null,
    int SynthesizedBitmapUsedBlocks = 0,
    int SynthesizedBitmapFreeBlocks = 0);

public sealed record DicHfsMasterDirectoryBlock(
    string VolumeName,
    ushort FileCountInRoot,
    ushort VolumeBitmapStartBlock,
    ushort AllocationBlockCount,
    uint AllocationBlockSize,
    ushort FirstAllocationBlock,
    uint NextCatalogNodeId,
    ushort FreeAllocationBlocks,
    uint ExtentsOverflowFileSize,
    IReadOnlyList<DicHfsExtentDescriptor> ExtentsOverflowExtents,
    uint CatalogFileSize,
    IReadOnlyList<DicHfsExtentDescriptor> CatalogExtents);

public sealed record DicHfsExtentDescriptor(ushort StartBlock, ushort BlockCount);

public sealed record DicSupplementaryDirectoryHint(
    string Path,
    uint ExtentLba,
    ushort ParentDirectoryNumber,
    int DirectoryNumber);

public sealed record SkeletonExtentSegment(
    uint ExtentLba,
    long DataLength,
    long PhysicalSectorCount,
    bool ContainsMode2Form2);

public sealed record SkeletonAlternateIsoRecord(
    uint ExtentLba,
    long DataLength);

public sealed record SkeletonDonorRequirement(
    string Path,
    uint ExtentLba,
    long DataLength,
    long PhysicalSectorCount,
    bool ContainsMode2Form2,
    byte FileFlags,
    int ExtendedAttributeRecordLength,
    int FileUnitSize,
    int InterleaveGapSize,
    string Reason,
    bool RequireRecordMatch = true,
    bool BlocksResurrection = true,
    bool RequiresRawDonor = false);

public sealed record SkeletonSourceImageExtent(long Lba, long Length);

public sealed record SkeletonSourceMatch(
    SkeletonContentEntry Entry,
    string SourcePath,
    string Sha1,
    bool IsXa,
    string MatchMethod = "SHA1",
    string? SourceRelativePath = null,
    long? SourceImageLba = null,
    long? SourceLength = null,
    byte[]? GeneratedPayload = null,
    IReadOnlyList<SkeletonSourceImageExtent>? SourceImageExtents = null,
    SkeletoolCatalogueMatchSource? CatalogueSource = null);

public sealed record SkeletonSourceScanProgress(
    int FilesProcessed,
    int FilesTotal,
    long BytesProcessed,
    long BytesTotal,
    string CurrentFile,
    string? MatchedEntryPath = null,
    string? MatchedSourcePath = null,
    bool MatchedAsXa = false,
    long BytesHashed = 0,
    int FilesHashed = 0,
    int FilesSkipped = 0,
    int FilesCached = 0)
{
    public double Fraction => BytesTotal <= 0 ? 0 : (double)BytesProcessed / BytesTotal;
}

public enum SkeletonResurrectionEventKind
{
    CopyingSkeleton,
    RestoringEntry,
    EntryRestored,
    Complete
}

public sealed record SkeletonResurrectionProgress(
    SkeletonResurrectionEventKind Kind,
    long BytesProcessed,
    long BytesTotal,
    string Message,
    string? EntryPath = null)
{
    public double Fraction => BytesTotal <= 0 ? 0 : (double)BytesProcessed / BytesTotal;
}

public sealed record SkeletonResurrectionResult(
    string OutputPath,
    int RestoredEntries,
    int MissingEntries,
    long OutputBytes);

internal sealed record RedumperDatTarget(
    string LogPath,
    string Name,
    long Size,
    string Crc32,
    string Md5,
    string Sha1);

/// <summary>
/// Rebuilds redumper skeleton images from their .hash manifest and matching source files.
/// This is an independent implementation based on the redumper skeleton/hash file behaviour
/// and the ISO9660/CD-ROM sector formats; it does not embed ResurrectSkeleton source code.
/// </summary>
public sealed partial class SkeletonResurrectionService
{
    public const int CookedSectorSize = 2048;
    public const int RawSectorSize = 2352;
    public const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
    public const string ZeroSystemAreaSha1 = "5188431849b4613152fd7bdba6a3ff0a4fd6424b";

    private const int SystemAreaSectors = 16;
    private const int HashBufferSize = 1024 * 1024;
    private const int CopyBufferSize = 4 * 1024 * 1024;
    private static readonly byte[] JolietCdWarningTemplate = Encoding.ASCII.GetBytes(
        "This CD-R contains a Joliet image and can only be read\r\n" +
        "by a system capable of reading Joliet images.\r\n");
    private const string HashCacheFileName = ".dumptoolbox_hashcache.json";
    private const int HashCacheVersion = 1;
    private const byte XaForm2Bit = 0x20;

    private static readonly Regex RedumperDatRomRegex = new(
        @"<rom\s+name=""(?<name>[^""]+)""\s+size=""(?<size>\d+)""\s+crc=""(?<crc>[0-9A-Fa-f]{8})""\s+md5=""(?<md5>[0-9A-Fa-f]{32})""\s+sha1=""(?<sha1>[0-9A-Fa-f]{40})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static ReadOnlySpan<byte> SyncPattern => CdRawSectorCodec.SyncPattern;

    private static readonly byte[] Cd001 = Encoding.ASCII.GetBytes("CD001");

    private sealed class HashCacheDocument
    {
        public int Version { get; set; }
        public List<HashCacheEntry> Files { get; set; } = new();
    }

    private sealed class HashCacheEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha1 { get; set; } = string.Empty;
    }

    private sealed record HashManifestEntry(string Sha1, string Path);
    private sealed record IsoFileExtent(string Path, uint Lba, uint Length, IReadOnlyList<SkeletonSourceImageExtent>? Extents = null)
    {
        public IReadOnlyList<SkeletonSourceImageExtent> LogicalExtents => Extents is { Count: > 0 }
            ? Extents
            : new[] { new SkeletonSourceImageExtent(Lba, Length) };
        public long LogicalLength => LogicalExtents.Sum(extent => extent.Length);
        public bool IsMultiExtent => LogicalExtents.Count > 1;
    }
    private sealed record IsoTree(
        string VolumeIdentifier,
        IReadOnlyList<IsoFileExtent> Files,
        IReadOnlyList<uint> AreaStarts)
    {
        public long GetGapPayloadLength(uint gapStart, int payloadBytesPerSector)
        {
            foreach (uint next in AreaStarts)
            {
                if (next > gapStart)
                    return checked((long)(next - gapStart) * payloadBytesPerSector);
            }
            return 0;
        }
    }
    private sealed record DirectoryRecord(uint Lba, uint DataLength, byte Flags, byte[] Identifier);

    private enum RawSectorPayloadKind
    {
        Unsupported,
        Mode1,
        Mode2Form1,
        Mode2Form2
    }

    private sealed class EntryBuilder
    {
        public EntryBuilder(string path, uint extentLba, long dataLength, SkeletonSpecialKind specialKind, bool canRestore)
        {
            Path = path;
            ExtentLba = extentLba;
            DataLength = dataLength;
            SpecialKind = specialKind;
            CanRestore = canRestore;
        }

        public string Path { get; }
        public uint ExtentLba { get; set; }
        public long DataLength { get; set; }
        public string? Sha1 { get; set; }
        public string? XaSha1 { get; set; }
        public SkeletonSpecialKind SpecialKind { get; }
        public bool CanRestore { get; }
        private readonly List<SkeletonAlternateIsoRecord> _alternateIsoRecords = new();

        public void AddAlternateIsoRecord(uint extentLba, long dataLength)
        {
            if (!_alternateIsoRecords.Any(record => record.ExtentLba == extentLba && record.DataLength == dataLength))
                _alternateIsoRecords.Add(new SkeletonAlternateIsoRecord(extentLba, dataLength));
        }

        public SkeletonContentEntry ToEntry() => new(
            Path, ExtentLba, DataLength, Sha1, XaSha1, SpecialKind, CanRestore,
            AlternateIsoRecords: _alternateIsoRecords.Count == 0 ? null : _alternateIsoRecords.ToArray());
    }

    private sealed class SkeletonImageReader : IAsyncDisposable
    {
        private readonly FileStream _stream;

        private SkeletonImageReader(FileStream stream, SkeletonImageKind kind, int baseLba, long sectorCount)
        {
            _stream = stream;
            Kind = kind;
            BaseLba = baseLba;
            SectorCount = sectorCount;
            SectorSize = kind == SkeletonImageKind.Raw2352 ? RawSectorSize : CookedSectorSize;
        }

        public SkeletonImageKind Kind { get; }
        public int SectorSize { get; }
        public int BaseLba { get; }
        public long SectorCount { get; }

        public static async Task<SkeletonImageReader> OpenAsync(string path, CancellationToken cancellationToken)
        {
            var stream = OpenRead(path, 256 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                long length = stream.Length;
                if (length <= 0)
                    throw new InvalidOperationException("Skeleton file is empty.");

                bool rawAligned = length % RawSectorSize == 0;
                bool cookedAligned = length % CookedSectorSize == 0;
                byte[] first = new byte[Math.Min(RawSectorSize, (int)Math.Min(length, RawSectorSize))];
                stream.Position = 0;
                await ReadExactlyAsync(stream, first, cancellationToken);

                if (rawAligned && first.Length >= 16 && first.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern))
                {
                    int minute = DecodeBcd(first[12]);
                    int second = DecodeBcd(first[13]);
                    int frame = DecodeBcd(first[14]);
                    int baseLba = checked((minute * 60 + second) * 75 + frame - 150);
                    return new SkeletonImageReader(stream, SkeletonImageKind.Raw2352, baseLba, length / RawSectorSize);
                }

                if (cookedAligned)
                    return new SkeletonImageReader(stream, SkeletonImageKind.Cooked2048, 0, length / CookedSectorSize);

                throw new InvalidOperationException("Skeleton is neither a valid 2048-byte ISO skeleton nor a sync-aligned 2352-byte raw CD skeleton.");
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }

        public async Task<byte[]> ReadForm1SectorAsync(long lba, CancellationToken cancellationToken)
        {
            if (Kind == SkeletonImageKind.Cooked2048)
            {
                long index = lba - BaseLba;
                if (index < 0 || index >= SectorCount)
                    throw new InvalidOperationException($"ISO LBA {lba:N0} is outside the skeleton.");
                byte[] cooked = new byte[CookedSectorSize];
                _stream.Position = index * CookedSectorSize;
                await ReadExactlyAsync(_stream, cooked, cancellationToken);
                return cooked;
            }

            long rawIndex = lba - BaseLba;
            if (rawIndex < 0 || rawIndex >= SectorCount)
                throw new InvalidOperationException($"Raw CD LBA {lba:N0} is outside the skeleton.");

            byte[] raw = new byte[RawSectorSize];
            _stream.Position = rawIndex * RawSectorSize;
            await ReadExactlyAsync(_stream, raw, cancellationToken);
            if (!raw.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern))
                throw new InvalidOperationException($"Invalid raw sector sync at LBA {lba:N0}.");

            if (raw[15] == 1)
                return raw.AsSpan(16, CookedSectorSize).ToArray();
            if (raw[15] == 2 && (raw[18] & XaForm2Bit) == 0)
                return raw.AsSpan(24, CookedSectorSize).ToArray();

            throw new InvalidOperationException($"ISO9660 metadata at LBA {lba:N0} is not stored in a Mode 1 / Mode 2 Form 1 sector.");
        }

        public async Task<byte[]> ReadForm1BytesAsync(uint lba, uint byteLength, CancellationToken cancellationToken)
        {
            if (byteLength == 0)
                return Array.Empty<byte>();
            long sectors = DivideRoundUp(byteLength, CookedSectorSize);
            byte[] result = new byte[checked((int)byteLength)];
            int written = 0;
            for (long i = 0; i < sectors; i++)
            {
                byte[] sector = await ReadForm1SectorAsync((long)lba + i, cancellationToken);
                int copy = Math.Min(CookedSectorSize, result.Length - written);
                sector.AsSpan(0, copy).CopyTo(result.AsSpan(written, copy));
                written += copy;
            }
            return result;
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
