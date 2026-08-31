using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private sealed record DicVolumeInfo(
        string VolumeIdentifier,
        long VolumeSpaceSize,
        IReadOnlyList<DicFileRecord> Files,
        IReadOnlyList<DicFileRecord> DonorOnlyRecords,
        IReadOnlySet<long> MetadataLbas,
        DateTimeOffset DefaultRecordingTime,
        long? PrimaryPathTableLba,
        long PrimaryPathTableSize,
        IReadOnlyList<DicPathTableRecord> PrimaryPathTableRecords,
        int PathsReconstructedFromIdentifiers,
        IReadOnlyList<DicSupplementaryDirectoryHint> SupplementaryDirectoryHints,
        IReadOnlyList<long> PrimaryDescriptorVolumeSpaceSizes,
        IReadOnlyList<long> SupplementaryDescriptorVolumeSpaceSizes);

    private sealed record DicPathTableRecord(
        byte IdentifierLength,
        byte ExtendedAttributeLength,
        uint ExtentLba,
        ushort ParentDirectoryNumber,
        byte[] Identifier);

    private sealed class DicPathTableRecordBuilder
    {
        public int IdentifierLength { get; set; } = -1;
        public int ExtendedAttributeLength { get; set; }
        public long ExtentLba { get; set; } = -1;
        public int ParentDirectoryNumber { get; set; } = -1;
        public string Identifier { get; set; } = string.Empty;
    }

    [Flags]
    private enum IsoDirectoryRecordFlags : byte
    {
        None = 0x00,
        Existence = 0x01,
        Directory = 0x02,
        Associated = 0x04,
        Record = 0x08,
        Protection = 0x10,
        MultiExtent = 0x80
    }

    private sealed record DicFileRecord(
        string Path,
        long ExtentLba,
        long DataLength,
        int ExtendedAttributeRecordLength,
        int FileUnitSize,
        int InterleaveGapSize,
        IReadOnlyList<string>? Aliases = null,
        DateTimeOffset? RecordingTime = null,
        IsoDirectoryRecordFlags Flags = IsoDirectoryRecordFlags.None,
        int Sequence = 0,
        string? OriginalPath = null,
        bool SupplementaryOnlyZeroLengthAlias = false,
        byte[]? SystemUse = null,
        byte[]? RawRecordingTime = null);

    private sealed class DicFileRecordBuilder
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileIdentifier { get; set; } = string.Empty;
        public long ExtentLba { get; set; } = -1;
        public long DataLength { get; set; } = -1;
        public IsoDirectoryRecordFlags Flags { get; set; }
        public int ExtendedAttributeRecordLength { get; set; }
        public int FileUnitSize { get; set; }
        public int InterleaveGapSize { get; set; }
        public DateTimeOffset? RecordingTime { get; set; }
        public bool Include { get; set; }
    }


    private sealed record CeQuadratLinkTableContext(
        long LinkTableLba,
        IReadOnlyList<uint> PrimaryDirectoryExtents,
        IReadOnlyDictionary<uint, uint>? ExistingJolietByPrimary = null);

    private sealed class JolietDirectoryNode
    {
        public JolietDirectoryNode(string name, JolietDirectoryNode? parent, PrimaryDirectoryMetadata metadata)
        {
            Name = name;
            Parent = parent;
            RecordingTime = metadata.RecordingTime;
            Flags = metadata.Flags;
            SystemUse = metadata.SystemUse;
            RawRecordingTime = metadata.RawRecordingTime;
            SelfRecordingTime = metadata.SelfRecordingTime ?? metadata.RecordingTime;
            SelfSystemUse = metadata.SelfSystemUse ?? metadata.SystemUse;
            SelfRawRecordingTime = metadata.SelfRawRecordingTime ?? metadata.RawRecordingTime;
            ParentLinkRecordingTime = metadata.ParentLinkRecordingTime ?? (parent?.SelfRecordingTime ?? SelfRecordingTime);
            ParentLinkSystemUse = metadata.ParentLinkSystemUse ?? (parent?.SelfSystemUse ?? SelfSystemUse);
            ParentLinkRawRecordingTime = metadata.ParentLinkRawRecordingTime ?? (parent?.SelfRawRecordingTime ?? SelfRawRecordingTime);
            PrimaryExtentLba = metadata.PrimaryExtentLba;
            PrimaryDataLength = metadata.PrimaryDataLength;
            PrimaryPath = metadata.PrimaryPath;
            PrimaryRecordOrder = metadata.PrimaryRecordOrder;
        }

        public string Name { get; }
        public JolietDirectoryNode? Parent { get; }
        // Parent-visible entry metadata.
        public DateTimeOffset RecordingTime { get; }
        public byte Flags { get; }
        public byte[]? SystemUse { get; }
        public byte[]? RawRecordingTime { get; }
        // Internal "." metadata.
        public DateTimeOffset SelfRecordingTime { get; }
        public byte[]? SelfSystemUse { get; set; }
        public byte[]? SelfRawRecordingTime { get; }
        // Internal ".." metadata as mastered in this directory.
        public DateTimeOffset ParentLinkRecordingTime { get; }
        public byte[]? ParentLinkSystemUse { get; set; }
        public byte[]? ParentLinkRawRecordingTime { get; }
        public long? PrimaryExtentLba { get; }
        public long? PrimaryDataLength { get; }
        public string? PrimaryPath { get; }
        public int PrimaryRecordOrder { get; }
        public Dictionary<string, JolietDirectoryNode> Children { get; } = new(StringComparer.Ordinal);
        public List<JolietFileNode> Files { get; } = new();
        public long ExtentLba { get; set; }
        public long DataLength { get; set; }
        public int DirectoryNumber { get; set; }
    }

    private sealed record JolietFileNode(string Name, long ExtentLba, long DataLength, DateTimeOffset RecordingTime, byte Flags, byte[]? SystemUse, byte[]? RawRecordingTime, int PrimaryRecordOrder, bool SupplementaryOnlyZeroLengthAlias = false);
    private sealed record JolietOutputRecord(int SortOrder, string SortName, uint ExtentLba, uint DataLength, byte Flags, byte[] Identifier, DateTimeOffset RecordingTime, byte[]? SystemUse, byte[]? RawRecordingTime, bool SupplementaryOnlyZeroLengthAlias = false);
    private sealed record PrimaryDirectoryMetadata(
        DateTimeOffset RecordingTime,
        byte Flags,
        byte[]? SystemUse = null,
        byte[]? RawRecordingTime = null,
        DateTimeOffset? SelfRecordingTime = null,
        byte[]? SelfSystemUse = null,
        byte[]? SelfRawRecordingTime = null,
        DateTimeOffset? ParentLinkRecordingTime = null,
        byte[]? ParentLinkSystemUse = null,
        byte[]? ParentLinkRawRecordingTime = null,
        long? PrimaryExtentLba = null,
        long? PrimaryDataLength = null,
        string? PrimaryPath = null,
        int PrimaryRecordOrder = int.MaxValue);

    private sealed class DicDiscTrackInfo
    {
        public int TrackNumber { get; set; }
        public bool IsAudio { get; set; }
        public int? Mode { get; set; }
        public long? StartLba { get; set; }
        public long? EndLba { get; set; }
        public long? Index0Lba { get; set; }
        public long? Index1Lba { get; set; }
        public long? DataStartLba { get; set; }
        public long? DataEndLba { get; set; }
    }

    private sealed class DicDiscInfo
    {
        public long? TrackStartLba { get; set; }
        public long? TrackEndLba { get; set; }
        public long? TrackSectorCount { get; set; }
        public int? TrackMode { get; set; }
        public bool IsXa { get; set; }
        public bool IsDvd { get; set; }
        public bool DvdIsSingleLayer { get; set; }
        public long? DvdSectorCount { get; set; }
        public long? DvdPfiStartingDataSector { get; set; }
        public long? DvdPfiEndDataSector { get; set; }
        public long? DvdPfiSectorCount { get; set; }
        public long? ImageSize { get; set; }
        public string? ImageCrc32 { get; set; }
        public string? ImageMd5 { get; set; }
        public string? ImageSha1 { get; set; }
        public List<DicDiscTrackInfo> Tracks { get; } = new();
    }

    private sealed record DicDatImageInfo(string Name, long Size, int SectorSize, string Crc32, string Md5, string Sha1);

    private enum SummaryListKind
    {
        None,
        EccEdc,
        InvalidMode,
        InvalidSync,
        ZeroSync,
        BadMsf,
        SubheaderMismatch,
        ExpectedZeroMismatch,
        Fill55
    }

    private sealed record DicSupplementalEccEdcVerification(
        string Path,
        DicEccEdcParseResult ParseResult);

    private sealed record DicExactRawSectorEvidence(
        IReadOnlyDictionary<long, byte[]> Sectors,
        IReadOnlyList<string> Paths,
        int IgnoredCandidateCount);

    private sealed record DicEccEdcParseResult(
        Dictionary<long, DicSectorLayout> Layouts,
        IReadOnlyList<long> EccErrorPhysicalLbas,
        IReadOnlyList<long> InvalidModePhysicalLbas,
        IReadOnlyList<long> ExplicitFill55PhysicalLbas,
        int ReportedEccErrorCount,
        int ReportedInvalidModeCount,
        int ReportedFill55Count,
        int UnmappedEccErrorCount,
        int UnmappedInvalidModeCount,
        int MalformedSectorRecordCount,
        long? StreamTruncatedAtPhysicalSector,
        int Fill55ExceptHeaderCount);

    private sealed record DicSectorLayout(
        int Mode,
        int Form,
        byte FileNumber,
        byte ChannelNumber,
        byte Submode,
        byte CodingInfo,
        bool HasEdc = true,
        long ReportedLba = 0,
        byte[]? RawHeaderOverride = null,
        byte[]? XaSubheaderOverride = null,
        bool IsAudio = false,
        bool IsUnknown = false,
        bool HasBlockIndicators = false,
        bool HasInvalidSync = false,
        bool HasZeroSync = false,
        bool HasBadMsf = false,
        bool HasMissingMsf = false,
        bool HasInvalidMode = false,
        bool HasEccMismatch = false,
        bool HasExplicitFill55 = false,
        bool IsDiscTrackFallback = false,
        bool SummaryInvalidMode = false,
        bool SummaryInvalidSync = false,
        bool SummaryZeroSync = false,
        bool SummaryBadMsf = false,
        bool SummarySubheaderMismatch = false,
        bool SummaryExpectedZeroMismatch = false)
    {
        public bool XaSubheaderCopiesDiffer =>
            XaSubheaderOverride is { Length: 8 } xa &&
            !xa.AsSpan(0, 4).SequenceEqual(xa.AsSpan(4, 4));

        public bool NeedsExactRawDonor =>
            IsAudio ||
            IsUnknown ||
            HasBlockIndicators ||
            HasMissingMsf ||
            SummaryInvalidMode ||
            HasInvalidSync ||
            HasZeroSync ||
            SummaryInvalidSync ||
            SummaryZeroSync ||
            XaSubheaderCopiesDiffer ||
            SummarySubheaderMismatch ||
            SummaryExpectedZeroMismatch;
    }

    private sealed record DicFileSlackRegion(
        string Path,
        long Lba,
        int SlackBytes,
        int PayloadCapacity,
        bool ContainsMode2Form2);

    private sealed record DicUnclaimedSectorRegion(long StartLba, long EndLba)
    {
        public long SectorCount => checked(EndLba - StartLba + 1);
    }

    private sealed record DicApplePartitionMapInfo(IReadOnlyList<DicApplePartitionEntry> Partitions);

    private sealed record DicApplePartitionEntry(uint StartBlock, uint BlockCount, string Name, string Type);

    private sealed record DicOffsetEvidenceResult(
        IReadOnlyDictionary<long, DicPayloadEvidence> Payloads,
        int CaptureSetCount,
        int FullyKnownSectors,
        int ConflictCount);

    private sealed class DicPayloadEvidence
    {
        public byte[] Data { get; } = new byte[CookedSectorSize];
        public bool[] Known { get; } = new bool[CookedSectorSize];
        private bool[] Conflicted { get; } = new bool[CookedSectorSize];

        public int KnownByteCount => Known.Count(value => value);
        public int ConflictCount => Conflicted.Count(value => value);
        public bool IsComplete => KnownByteCount == CookedSectorSize;
        public bool KnownBytesAreZero
        {
            get
            {
                for (int i = 0; i < Data.Length; i++)
                {
                    if (Known[i] && Data[i] != 0)
                        return false;
                }
                return true;
            }
        }

        public void Merge(int offset, byte value)
        {
            if ((uint)offset >= CookedSectorSize || Conflicted[offset])
                return;

            if (!Known[offset])
            {
                Data[offset] = value;
                Known[offset] = true;
                return;
            }

            if (Data[offset] == value)
                return;

            Known[offset] = false;
            Conflicted[offset] = true;
            Data[offset] = 0;
        }
    }

    private sealed class RawCaptureBuffer
    {
        public RawCaptureBuffer(long lba) => Lba = lba;
        public long Lba { get; }
        public byte[] Data { get; } = new byte[RawSectorSize];
        public bool[] Written { get; } = new bool[RawSectorSize];
    }

    private sealed class MetadataBuffer
    {
        public byte[] Data { get; } = new byte[CookedSectorSize];
        public bool[] Written { get; } = new bool[CookedSectorSize];
    }
}
