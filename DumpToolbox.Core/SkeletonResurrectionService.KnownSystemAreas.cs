using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private const string MacintoshRomMaker102 = "The Personal RomMaker for the Macintosh, Version 1.02";
    private const string DummyAppleVolume = "Dummy Apple Volume";
    private const string KnownRivenFfSystemAreaSha1 = "b9aec682b6a6e7101af5253f4fdcf1134af0b57b";

    private static readonly string[] KnownRomMakerSectorTexts =
    [
        MacintoshRomMaker102,
        "The Personal RomMaker (PC RomMaker V1.51)",
        "The Personal RomMaker (PC Archiver V1.05A)",
        "The Personal RomMaker (PC), Version 1.30",
        "The Personal RomMaker (PC Archiver V1.05D)",
        "The Personal RomMaker (PC Archiver V1.05E)",
        "The Personal RomMaker (PC RomMaker V1.53A)",
        "The Personal RomMaker (PC RomMaker V2.02)",
        "The Personal RomMaker (32-bit Windows 95 V4.12)",
        "The Personal RomMaker (Windows V1.03)",
        "The Personal RomMaker (Windows V2.00)",
        "The Personal RomMaker (PC RomMaker V1.40)",
        "The Personal RomMaker (PC RomMaker V1.54)",
        "The Personal RomMaker (Windows V1.00)",
        "The Personal RomMaker (PC Mixed Mode  V1.00C)",
        "The Personal RomMaker (Windows V3.01)",
        "The Personal RomMaker (PC RomMaker V1.53)",
        "The Personal RomMaker (Windows V2.01)",
        "The Personal RomMaker (32-bit Windows 95 V4.20)",
        "The Personal RomMaker (32-bit Windows 95 V4.10)"
    ];

    // The Macintosh RomMaker corpus uses a small set of physical-media block counts.
    // The remainder of the partition geometry is recovered from the surviving HFS MDB
    // and ISO volume size, then the complete 32 KiB candidate is verified by SHA-1.
    private static readonly uint[] KnownMacintoshDdmBlockCounts =
    [
        1_308_929,
        1_333_316,
        2_042_264,
        2_059_139,
        2_061_107,
        2_109_375,
        2_110_811,
        2_117_024,
        2_131_991,
        3_933_039
    ];

    private static readonly int[] AppleVolumeTailCandidates =
    [
        3, 2, 4, 1, 0, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32
    ];

    public static bool CanRecoverKnownSystemArea(SkeletonInspectionResult inspection)
    {
        KnownSystemAreaRecoveryInfo? info = inspection.KnownSystemAreaRecovery;
        if (info is null || info.Payload.Length != SystemAreaSectors * CookedSectorSize)
            return false;

        return inspection.Entries.Any(entry =>
            entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
            string.Equals(entry.Sha1, info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanGenerateSystemArea(SkeletonInspectionResult inspection) =>
        CanRecoverNeroSystemArea(inspection) || CanRecoverKnownSystemArea(inspection);

    public static SkeletonSourceMatch RecoverKnownSystemArea(SkeletonInspectionResult inspection)
    {
        KnownSystemAreaRecoveryInfo info = inspection.KnownSystemAreaRecovery
            ?? throw new InvalidOperationException("The skeleton does not match a supported repeatable SYSTEM_AREA layout.");
        SkeletonContentEntry entry = inspection.Entries.FirstOrDefault(candidate =>
            candidate.SpecialKind == SkeletonSpecialKind.SystemArea &&
            string.Equals(candidate.Sha1, info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The known SYSTEM_AREA recovery information does not match the manifest entry.");

        byte[] payload = info.Payload.ToArray();
        string actualSha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        if (!actualSha1.Equals(info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The generated known SYSTEM_AREA failed its final SHA-1 verification.");

        return new SkeletonSourceMatch(
            entry,
            $"Generated known system area: {info.PatternName}",
            actualSha1,
            IsXa: false,
            MatchMethod: "Known repeatable system-area reconstruction",
            SourceRelativePath: info.PatternName,
            SourceLength: payload.LongLength,
            GeneratedPayload: payload);
    }

    private static async Task<KnownSystemAreaRecoveryInfo?> TryCreateKnownSystemAreaRecoveryAsync(
        SkeletonImageReader image,
        uint volumeSpaceSize,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        if (expectedSha1.Length != 40 || !expectedSha1.All(Uri.IsHexDigit))
            return null;

        foreach (string text in KnownRomMakerSectorTexts)
        {
            KnownSystemAreaRecoveryInfo? match = MatchKnownPayload(
                BuildRepeatedTextSystemArea(text, firstSector: 1),
                expectedSha1,
                $"{text}; sectors 1-15");
            if (match is not null)
                return match;

            match = MatchKnownPayload(
                BuildRepeatedTextSystemArea(text, firstSector: 0),
                expectedSha1,
                $"{text}; sectors 0-15");
            if (match is not null)
                return match;
        }

        KnownSystemAreaRecoveryInfo? fixedMatch = MatchKnownPayload(
            BuildRepeatedDummyAppleSystemArea(firstSector: 1),
            expectedSha1,
            "Dummy Apple Volume; sectors 1-15");
        if (fixedMatch is not null)
            return fixedMatch;

        fixedMatch = MatchKnownPayload(
            BuildRepeatedDummyAppleSystemArea(firstSector: 0),
            expectedSha1,
            "Dummy Apple Volume; sectors 0-15");
        if (fixedMatch is not null)
            return fixedMatch;

        if (expectedSha1.Equals(KnownRivenFfSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
        {
            fixedMatch = MatchKnownPayload(
                BuildKnownRivenFfSystemArea(),
                expectedSha1,
                "Riven classic-HFS bitmap layout; FF sectors 5-7");
            if (fixedMatch is not null)
                return fixedMatch;
        }

        byte[] allFf = new byte[SystemAreaSectors * CookedSectorSize];
        Array.Fill(allFf, (byte)0xFF);
        fixedMatch = MatchKnownPayload(allFf, expectedSha1, "all-FF system area");
        if (fixedMatch is not null)
            return fixedMatch;

        if (image.BaseLba != 0 || volumeSpaceSize <= SystemAreaSectors)
            return null;

        IReadOnlyList<uint> hfsStarts = await FindClassicHfsPartitionStartsAsync(
            image,
            volumeSpaceSize,
            cancellationToken).ConfigureAwait(false);
        return TryBuildKnownAppleSystemAreaForGeometry(expectedSha1, volumeSpaceSize, hfsStarts);
    }

    internal static KnownSystemAreaRecoveryInfo? TryBuildKnownAppleSystemAreaForGeometry(
        string expectedSha1,
        uint volumeSpaceSize,
        IEnumerable<uint> hfsPartitionStarts)
    {
        ulong volumeBlocks64 = (ulong)volumeSpaceSize * 4;
        if (volumeBlocks64 > uint.MaxValue)
            return null;
        uint volumeBlocks = (uint)volumeBlocks64;

        foreach (uint hfsStart in hfsPartitionStarts.Distinct().OrderBy(value => value))
        {
            if (hfsStart <= 4 || hfsStart >= volumeBlocks)
                continue;

            foreach (int tailBlocks in AppleVolumeTailCandidates)
            {
                if ((uint)tailBlocks >= volumeBlocks - hfsStart)
                    continue;
                uint hfsEnd = volumeBlocks - (uint)tailBlocks;
                uint hfsSize = hfsEnd - hfsStart;
                IReadOnlyList<uint> ddmCandidates = BuildAppleDdmCandidates(hfsEnd);

                for (int pattern = 0; pattern < 2; pattern++)
                {
                    bool romMaker = pattern == 0;
                    string name = romMaker ? "Personal RomMaker Macintosh 1.02" : "Dummy Apple Volume";
                    byte[] payload = romMaker
                        ? BuildRepeatedTextSystemArea(MacintoshRomMaker102, firstSector: 1)
                        : BuildRepeatedDummyAppleSystemArea(firstSector: 1);
                    for (int marker = 0; marker < 2; marker++)
                    {
                        bool includeMrks = marker == 1;
                        BuildThreeEntryApplePartitionMap(
                            payload.AsSpan(0, CookedSectorSize),
                            0,
                            hfsStart,
                            hfsSize,
                            includeMrks);

                        foreach (uint ddmBlocks in ddmCandidates)
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), ddmBlocks);
                            KnownSystemAreaRecoveryInfo? match = MatchKnownPayload(
                                payload,
                                expectedSha1,
                                $"Apple partition map + {name}; HFS blocks {hfsStart:N0}-{hfsEnd:N0}" +
                                (includeMrks ? "; MRKS marker" : string.Empty));
                            if (match is not null)
                                return match;
                        }
                    }
                }
            }
        }

        return null;
    }

    internal static byte[] BuildRepeatedTextSystemArea(string text, int firstSector)
    {
        if (firstSector is < 0 or >= SystemAreaSectors)
            throw new ArgumentOutOfRangeException(nameof(firstSector));

        // The Macintosh 1.02 writer appends a line-feed; the PC/Windows variants do
        // not. That byte is part of the known sector hash, not display whitespace.
        byte[] sector = Encoding.ASCII.GetBytes(
            text.Equals(MacintoshRomMaker102, StringComparison.Ordinal) ? text + "\n" : text);
        if (sector.Length > CookedSectorSize)
            throw new ArgumentException("The known system-area text does not fit in one sector.", nameof(text));

        return BuildRepeatedSectorSystemArea(sector, firstSector);
    }

    internal static byte[] BuildRepeatedDummyAppleSystemArea(int firstSector)
    {
        byte[] sector = new byte[CookedSectorSize];
        WriteApplePartitionEntry(
            sector,
            1,
            1,
            1,
            DummyAppleVolume,
            "Apple_partition_map",
            0x13);
        return BuildRepeatedSectorSystemArea(sector, firstSector);
    }

    private static byte[] BuildRepeatedSectorSystemArea(ReadOnlySpan<byte> sector, int firstSector)
    {
        if (firstSector is < 0 or >= SystemAreaSectors)
            throw new ArgumentOutOfRangeException(nameof(firstSector));
        if (sector.Length > CookedSectorSize)
            throw new ArgumentException("The known system-area sector is too large.", nameof(sector));

        byte[] payload = new byte[SystemAreaSectors * CookedSectorSize];
        for (int lba = firstSector; lba < SystemAreaSectors; lba++)
            sector.CopyTo(payload.AsSpan(lba * CookedSectorSize, sector.Length));
        return payload;
    }

    internal static byte[] BuildThreeEntryAppleSystemArea(
        string repeatedSectorText,
        uint ddmBlockCount,
        uint hfsStart,
        uint hfsSize,
        bool includeMrks)
    {
        byte[] payload = repeatedSectorText.Equals(DummyAppleVolume, StringComparison.Ordinal)
            ? BuildRepeatedDummyAppleSystemArea(firstSector: 1)
            : BuildRepeatedTextSystemArea(repeatedSectorText, firstSector: 1);
        BuildThreeEntryApplePartitionMap(
            payload.AsSpan(0, CookedSectorSize),
            ddmBlockCount,
            hfsStart,
            hfsSize,
            includeMrks);
        return payload;
    }

    private static void BuildThreeEntryApplePartitionMap(
        Span<byte> sector,
        uint ddmBlockCount,
        uint hfsStart,
        uint hfsSize,
        bool includeMrks)
    {
        sector.Clear();
        WriteDriverDescriptor(sector, ddmBlockCount);
        WriteApplePartitionEntry(sector.Slice(512, 512), 3, 1, 3, string.Empty, "Apple_partition_map", 0x13);
        WriteApplePartitionEntry(sector.Slice(1024, 512), 3, 4, hfsStart - 4, string.Empty, "ISO", 0x13);
        WriteApplePartitionEntry(sector.Slice(1536, 512), 3, hfsStart, hfsSize, string.Empty, "Apple_HFS", 0x13);
        if (includeMrks)
            "MRKS"u8.CopyTo(sector.Slice(1020, 4));
    }

    internal static byte[] BuildKnownRivenFfSystemArea()
    {
        const uint hfsStart = 16;
        const uint hfsSize = 1_320_164;
        const uint ddmBlocks = hfsStart + hfsSize;

        byte[] payload = new byte[SystemAreaSectors * CookedSectorSize];
        Span<byte> sector0 = payload.AsSpan(0, CookedSectorSize);
        WriteDriverDescriptor(sector0, ddmBlocks);
        WriteApplePartitionEntry(sector0.Slice(512, 512), 2, 1, 2, "Apple", "Apple_partition_map", 0x33);
        WriteApplePartitionEntry(sector0.Slice(1024, 512), 2, hfsStart, hfsSize, "Riven1", "Apple_HFS", 0x33);
        sector0[9] = 1;
        sector0[11] = 1;
        sector0[510] = 0x55;
        sector0[511] = 0xAA;
        BinaryPrimitives.WriteUInt32BigEndian(sector0.Slice(512 + 84, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(sector0.Slice(1024 + 84, 4), hfsSize);

        byte[] primaryMdbPrefix = Convert.FromHexString(
            "4244B7DA3E7CB7DA3E80818000080003C1A3D6DD000030000000C000002000000199000006526976" +
            "656E310000000000000000000000000000000000000000000000000000000011A765004E9800009D" +
            "30000008000001710000001800000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000004E9000D1EF01A30000000000000000009D2000D3920346000000000000");
        primaryMdbPrefix.CopyTo(payload, 18 * 512);
        payload.AsSpan(19 * 512, 14 * 512).Fill(0xFF);
        return payload;
    }

    private static void WriteDriverDescriptor(Span<byte> sector, uint blockCount)
    {
        sector[0] = 0x45;
        sector[1] = 0x52;
        BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(2, 2), 512);
        BinaryPrimitives.WriteUInt32BigEndian(sector.Slice(4, 4), blockCount);
    }

    private static void WriteApplePartitionEntry(
        Span<byte> entry,
        uint mapEntryCount,
        uint startBlock,
        uint blockCount,
        string name,
        string type,
        uint status)
    {
        entry.Clear();
        entry[0] = 0x50;
        entry[1] = 0x4D;
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4, 4), mapEntryCount);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), startBlock);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(12, 4), blockCount);
        WriteAsciiField(entry.Slice(16, 32), name);
        WriteAsciiField(entry.Slice(48, 32), type);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(88, 4), status);
    }

    private static void WriteAsciiField(Span<byte> destination, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
    }

    private static IReadOnlyList<uint> BuildAppleDdmCandidates(uint hfsEnd)
    {
        var candidates = new List<uint>(320);
        var seen = new HashSet<uint>();
        void Add(ulong value)
        {
            if (value <= uint.MaxValue && seen.Add((uint)value))
                candidates.Add((uint)value);
        }

        foreach (uint value in KnownMacintoshDdmBlockCounts)
            Add(value);
        for (uint delta = 1_200; delta <= 1_500; delta++)
            Add((ulong)hfsEnd + delta);
        Add((ulong)hfsEnd + 127_071);
        Add(hfsEnd);
        return candidates;
    }

    private static KnownSystemAreaRecoveryInfo? MatchKnownPayload(
        byte[] payload,
        string expectedSha1,
        string patternName)
    {
        string actual = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        return actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase)
            ? new KnownSystemAreaRecoveryInfo(patternName, actual, payload.ToArray())
            : null;
    }

    private static async Task<IReadOnlyList<uint>> FindClassicHfsPartitionStartsAsync(
        SkeletonImageReader image,
        uint volumeSpaceSize,
        CancellationToken cancellationToken)
    {
        ulong volumeBlocks = (ulong)volumeSpaceSize * 4;
        long maximumBlock = (long)Math.Min(volumeBlocks - 1, 8_192UL);
        long maximumSector = maximumBlock / 4;
        var starts = new List<uint>();

        for (long sectorIndex = SystemAreaSectors; sectorIndex <= maximumSector; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sectorIndex >= image.SectorCount)
                break;

            byte[] sector;
            try
            {
                sector = await image.ReadForm1SectorAsync(image.BaseLba + sectorIndex, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
            {
                continue;
            }

            for (int quarter = 0; quarter < 4; quarter++)
            {
                long blockIndex = sectorIndex * 4 + quarter;
                if (blockIndex < 66 || !IsPlausibleClassicHfsMasterDirectoryBlock(
                        sector,
                        quarter * 512,
                        blockIndex,
                        volumeBlocks))
                    continue;

                uint start = checked((uint)(blockIndex - 2));
                if (!starts.Contains(start))
                    starts.Add(start);
                if (starts.Count >= 8)
                    return starts;
            }
        }

        return starts;
    }

    private static bool IsPlausibleClassicHfsMasterDirectoryBlock(
        byte[] sector,
        int offset,
        long blockIndex,
        ulong volumeBlocks)
    {
        ReadOnlySpan<byte> block = sector.AsSpan(offset, 512);
        if (block[0] != 0x42 || block[1] != 0x44)
            return false;

        ushort allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(18, 2));
        uint allocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(20, 4));
        ushort firstAllocationBlock = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(28, 2));
        int volumeNameLength = block[36];
        if (allocationBlocks == 0 || allocationBlockSize < 512 || allocationBlockSize % 512 != 0 ||
            firstAllocationBlock == 0 || volumeNameLength is < 1 or > 27)
        {
            return false;
        }

        ulong partitionStart = checked((ulong)(blockIndex - 2));
        ulong allocationEnd = partitionStart + firstAllocationBlock +
                              (ulong)allocationBlocks * (allocationBlockSize / 512);
        return allocationEnd <= volumeBlocks + 4_096;
    }
}
