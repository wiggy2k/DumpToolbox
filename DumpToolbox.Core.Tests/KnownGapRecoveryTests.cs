using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DumpToolbox.Core;
using SharpCompress.Compressors.ZStandard;

namespace DumpToolbox.Core.Tests;

public sealed class KnownGapRecoveryTests
{
    [Theory]
    [InlineData("TOAST 2.5 Partition", 2, 0x13)]
    [InlineData("Toast 3.0.5 PPC HFS Optimizer", 2, 0x13)]
    [InlineData("Toast 4.1.3 HFS Optimizer", 2, 0x13)]
    [InlineData("Toast 5.2.3 HFS Optimizer", 2, 0x33)]
    [InlineData("Toast 6.0.3 HFS Optimizer", 2, 0x33)]
    [InlineData("Toast 7.0 HFS Optimizer", 2, 0x33)]
    [InlineData("Toast 9.0.1 HFS Optimizer", 35, 0x33)]
    [InlineData("Toast 9.0.5 HFS Optimizer", 35, 0x33)]
    public async Task ToastClassicHfsRuleSupportsObservedVersionFamiliesWithoutInventingHfsOnlyContent(
        string partitionName,
        int partitionMapBlocks,
        int status)
    {
        const int trackBaseLba = 51199;
        const int trackSectors = 851;
        const uint volumeEndLba = 51898;
        const uint rootLba = 51220;
        const uint firstFileLba = 51298;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-toast-hfs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "track.skeleton");
        string hashPath = Path.Combine(root, "track.hash");

        try
        {
            byte[] cooked = BuildSplitTrackIso(
                trackSectors,
                trackBaseLba,
                volumeEndLba,
                rootLba,
                firstFileLba);
            AddToastClassicHfsEvidence(cooked, trackBaseLba, volumeEndLba);
            byte[] expectedSystemArea = BuildToastClassicSystemArea(
                partitionName,
                checked((uint)partitionMapBlocks),
                checked((uint)status));

            await File.WriteAllBytesAsync(skeletonPath, ToRawMode1(cooked, trackBaseLba));
            await File.WriteAllTextAsync(
                hashPath,
                $"{Sha1(expectedSystemArea)} SYSTEM_AREA{Environment.NewLine}" +
                $"{new string('1', 40)} GAP_0000023{Environment.NewLine}");

            SkeletonInspectionResult inspection = await new SkeletonResurrectionService()
                .InspectAsync(skeletonPath, hashPath);

            ToastHybridRecoveryAssessment assessment = Assert.IsType<ToastHybridRecoveryAssessment>(
                inspection.ToastHybridRecovery);
            Assert.Equal("GAP_0000023", assessment.GapPath);
            Assert.Equal("GoM", assessment.HfsVolumeName);
            Assert.Equal(51229, assessment.HfsPartitionStartLba);
            Assert.Equal(0, assessment.HfsPartitionStartByteOffset);
            Assert.Equal(2676u, assessment.HfsPartitionBlockCount);
            Assert.Equal(58, assessment.UnexplainedAllocatedSectors);
            Assert.Equal(19u, assessment.HfsFileCount);
            Assert.Equal(3u, assessment.HfsDirectoryCount);
            Assert.Equal(51231, assessment.HfsCatalogStartLba);
            Assert.Equal(0, assessment.HfsCatalogStartByteOffset);
            Assert.Equal(18_432u, assessment.HfsCatalogFileSize);
            Assert.True(assessment.HfsCatalogCoveredByGap);
            Assert.False(assessment.CanReconstructExactly);
            Assert.Empty(inspection.KnownGapRecoveries);
            Assert.Equal(expectedSystemArea, inspection.KnownSystemAreaRecovery?.Payload);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ToastClassicHfsRuleCopiesSurvivingAlternateMdbAndRecognizesZeroTail()
    {
        const int trackBaseLba = 51199;
        const int trackSectors = 851;
        const uint volumeEndLba = 51898;
        const uint rootLba = 51220;
        const uint firstFileLba = 51298;
        const int firstGapRelativeLba = 23;
        const int firstGapSectors = 76;
        const int tailRelativeLba = 699;
        const int tailSectors = 152;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-toast-mdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "track.skeleton");
        string hashPath = Path.Combine(root, "track.hash");
        string outputPath = Path.Combine(root, "track-resurrected.bin");

        try
        {
            byte[] cooked = BuildSplitTrackIso(
                trackSectors,
                trackBaseLba,
                volumeEndLba,
                rootLba,
                firstFileLba);
            AddToastClassicHfsEvidence(cooked, trackBaseLba, volumeEndLba);

            byte[] expectedCooked = (byte[])cooked.Clone();
            int alternateMdbOffset = checked(((int)volumeEndLba - trackBaseLba - 1) * 2048 + 1024);
            int primaryMdbOffset = checked((51229 - trackBaseLba) * 2048 + 1024);
            Buffer.BlockCopy(expectedCooked, alternateMdbOffset, expectedCooked, primaryMdbOffset, 512);
            byte[] expectedGap = expectedCooked.AsSpan(
                firstGapRelativeLba * 2048,
                firstGapSectors * 2048).ToArray();

            await File.WriteAllBytesAsync(skeletonPath, ToRawMode1(cooked, trackBaseLba));
            await File.WriteAllTextAsync(
                hashPath,
                $"{Sha1(expectedGap)} GAP_{firstGapRelativeLba:D7}{Environment.NewLine}" +
                $"{Sha1(new byte[tailSectors * 2048])} GAP_{tailRelativeLba:D7}{Environment.NewLine}");

            var service = new SkeletonResurrectionService();
            SkeletonInspectionResult inspection = await service.InspectAsync(skeletonPath, hashPath);

            Assert.Equal(2, inspection.KnownGapRecoveries.Count);
            KnownGapRecoveryInfo metadata = Assert.Single(
                inspection.KnownGapRecoveries,
                recovery => recovery.Path == $"GAP_{firstGapRelativeLba:D7}");
            Assert.Equal(expectedGap, metadata.GeneratedPayload);
            Assert.Contains("alternate MDB", metadata.PatternName, StringComparison.Ordinal);

            KnownGapRecoveryInfo tail = Assert.Single(
                inspection.KnownGapRecoveries,
                recovery => recovery.Path == $"GAP_{tailRelativeLba:D7}");
            Assert.Null(tail.GeneratedPayload);
            Assert.Equal("Toast 152-sector zero post-HFS tail", tail.PatternName);

            SkeletonResurrectionResult result = await service.ResurrectAsync(
                inspection,
                new Dictionary<string, SkeletonSourceMatch>(),
                outputPath,
                allowMissing: false);

            Assert.Equal(0, result.MissingEntries);
            Assert.Equal(ToRawMode1(expectedCooked, trackBaseLba), await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SplitTrackGapManifestLbasAreResolvedAgainstRawTrackBase()
    {
        const int trackBaseLba = 51199;
        const int trackSectors = 851;
        const uint rootLba = 51220;
        const uint firstFileLba = 51298;
        const uint volumeEndLba = 51898;
        const uint firstGapRelativeLba = 23;
        const uint secondGapRelativeLba = 699;
        const int firstGapSectors = 76;
        const int secondGapSectors = 152;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-split-track-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "track.skeleton");
        string sourcePath = Path.Combine(root, "track.bin");
        string hashPath = Path.Combine(root, "track.hash");

        try
        {
            byte[] cooked = BuildSplitTrackIso(
                trackSectors,
                trackBaseLba,
                volumeEndLba,
                rootLba,
                firstFileLba);
            byte[] raw = ToRawMode1(cooked, trackBaseLba);
            await File.WriteAllBytesAsync(skeletonPath, raw);
            await File.WriteAllBytesAsync(sourcePath, raw);
            await File.WriteAllTextAsync(
                hashPath,
                $"{Sha1(new byte[firstGapSectors * 2048])} GAP_{firstGapRelativeLba:D7}{Environment.NewLine}" +
                $"{Sha1(new byte[secondGapSectors * 2048])} GAP_{secondGapRelativeLba:D7}{Environment.NewLine}");

            var service = new SkeletonResurrectionService();
            SkeletonInspectionResult inspection = await service.InspectAsync(skeletonPath, hashPath);

            Assert.Equal(trackBaseLba, inspection.BaseLba);
            SkeletonContentEntry firstGap = Assert.Single(
                inspection.Entries,
                entry => entry.Path == $"GAP_{firstGapRelativeLba:D7}");
            Assert.Equal((uint)(trackBaseLba + firstGapRelativeLba), firstGap.ExtentLba);
            Assert.Equal(firstGapSectors * 2048L, firstGap.DataLength);

            SkeletonContentEntry secondGap = Assert.Single(
                inspection.Entries,
                entry => entry.Path == $"GAP_{secondGapRelativeLba:D7}");
            Assert.Equal((uint)(trackBaseLba + secondGapRelativeLba), secondGap.ExtentLba);
            Assert.Equal(secondGapSectors * 2048L, secondGap.DataLength);

            Assert.Equal(2, inspection.KnownGapRecoveries.Count);
            IReadOnlyDictionary<string, SkeletonSourceMatch> matches =
                await service.MatchSourceImageAsync(inspection, sourcePath, useHistoryDatabase: false);
            Assert.Equal(2, matches.Count);
            Assert.All(matches.Values, match =>
                Assert.Contains("image GAP logical payload SHA1", match.MatchMethod, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RedumperZeroFill55AndInvalidSectorGapIsGeneratedAndRestoredExactly()
    {
        const int imageSectors = 26;
        const int volumeSectors = 22;
        const uint rootLba = 20;
        const uint gapLba = 22;
        const int gapSectors = 4;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-known-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "disc.skeleton");
        string hashPath = Path.Combine(root, "disc.hash");
        string outputPath = Path.Combine(root, "resurrected.bin");
        string subcodePath = Path.Combine(root, "disc.subcode.zst");

        try
        {
            byte[] cooked = BuildMinimalIso(imageSectors, volumeSectors, rootLba);
            byte[] expectedRaw = ToRawMode1(cooked);

            // The third physical GAP sector has an invalid literal mode byte. Redumper
            // omits it from the logical GAP hash and skeleton erasure leaves it intact.
            int invalidOffset = checked((int)((gapLba + 2) * 2352));
            expectedRaw[invalidOffset + 15] = 0xE1;
            expectedRaw.AsSpan(invalidOffset + 16, 2336).Fill(0xA7);

            // A read-error replacement generated by Redumper retains sync/header and
            // fills every byte after byte 15 with 0x55.
            int generatedOffset = checked((int)((gapLba + 3) * 2352));
            expectedRaw.AsSpan(generatedOffset + 16, 2336).Fill(0x55);

            byte[] skeletonRaw = (byte[])expectedRaw.Clone();
            EraseRedumperMode1Payload(skeletonRaw, gapLba);
            EraseRedumperMode1Payload(skeletonRaw, gapLba + 1);
            EraseRedumperMode1Payload(skeletonRaw, gapLba + 3);
            await File.WriteAllBytesAsync(skeletonPath, skeletonRaw);

            byte[] logicalHashStream = new byte[3 * 2048];
            logicalHashStream.AsSpan(2 * 2048, 2048).Fill(0x55);
            string gapSha1 = Sha1(logicalHashStream);
            await File.WriteAllTextAsync(hashPath, $"{gapSha1} GAP_{gapLba:D7}{Environment.NewLine}");
            await WriteCompressedRedumperSubcodeAsync(subcodePath, gapLba);

            var service = new SkeletonResurrectionService();
            SkeletonInspectionResult inspection = await service.InspectAsync(skeletonPath, hashPath);

            KnownGapRecoveryInfo recovery = Assert.Single(inspection.KnownGapRecoveries);
            Assert.Equal($"GAP_{gapLba:D7}", recovery.Path);
            Assert.Equal(gapSectors, recovery.SectorCount);
            Assert.Equal(3, recovery.Form1SectorCount);
            Assert.Equal(0, recovery.Form2SectorCount);
            Assert.Equal(1, recovery.PreservedSectorCount);
            Assert.Contains((long)gapLba + 3, recovery.GeneratedFill55Lbas);

            GapSubchannelEvidence evidence = Assert.Single(inspection.GapSubchannelEvidence);
            Assert.Equal($"GAP_{gapLba:D7}", evidence.GapPath);
            Assert.Contains("Zstandard", evidence.SourceFormat, StringComparison.Ordinal);
            Assert.Equal(gapSectors, evidence.FramesObserved);
            Assert.Equal(1, evidence.TrackNumber);
            Assert.Equal(1, evidence.IndexNumber);
            Assert.True(evidence.TrackIndexContinuous);
            Assert.False(evidence.HasTrackIndexBoundaryAtStart);
            Assert.Equal(1, evidence.InvalidQCrcCount);
            Assert.Contains((long)gapLba + 2, evidence.InvalidQCrcLbas);
            Assert.Equal(1, evidence.QTimeDiscontinuityCount);
            Assert.Equal((long)gapLba + 5, evidence.LeadOutStartLba);
            Assert.Equal(1, evidence.NonZeroRwFrames);
            Assert.Empty(inspection.SubchannelEvidenceWarnings);

            SkeletonResurrectionResult result = await service.ResurrectAsync(
                inspection,
                new Dictionary<string, SkeletonSourceMatch>(),
                outputPath,
                allowMissing: false);

            Assert.Equal(0, result.MissingEntries);
            Assert.Equal(expectedRaw, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task KnownGapIsNotOfferedWhenGeneratedPatternDoesNotMatchManifestSha1()
    {
        const int imageSectors = 24;
        const int volumeSectors = 22;
        const uint rootLba = 20;
        const uint gapLba = 22;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-unknown-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "disc.skeleton");
        string hashPath = Path.Combine(root, "disc.hash");

        try
        {
            byte[] raw = ToRawMode1(BuildMinimalIso(imageSectors, volumeSectors, rootLba));
            EraseRedumperMode1Payload(raw, gapLba);
            EraseRedumperMode1Payload(raw, gapLba + 1);
            await File.WriteAllBytesAsync(skeletonPath, raw);
            await File.WriteAllTextAsync(
                hashPath,
                $"{new string('1', 40)} GAP_{gapLba:D7}{Environment.NewLine}");

            var service = new SkeletonResurrectionService();
            SkeletonInspectionResult inspection = await service.InspectAsync(skeletonPath, hashPath);

            Assert.Empty(inspection.KnownGapRecoveries);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void EraseRedumperMode1Payload(byte[] raw, long lba)
    {
        int offset = checked((int)(lba * 2352));
        raw.AsSpan(offset + 16, 2052).Clear();
        raw.AsSpan(offset + 2076, 276).Clear();
    }

    private static async Task WriteCompressedRedumperSubcodeAsync(string path, long gapLba)
    {
        long lastLba = gapLba + 5;
        int sectorCount = checked((int)(lastLba - SubchannelTextService.RedumperFirstLba + 1));
        byte[] multiplexed = new byte[checked(sectorCount * SubchannelTextService.BytesPerSector)];

        for (long lba = gapLba - 1; lba <= gapLba + 4; lba++)
        {
            byte[] row = BuildPositionSubchannelRow(lba, track: 1, index: 1);
            if (lba == gapLba + 1)
                row[24] = 0x01;
            if (lba == gapLba + 2)
                row[19] = 0x01; // Corrupt absolute minute after CRC generation.
            WriteMultiplexedRow(multiplexed, lba, row);
        }

        byte[] leadOut = BuildPositionSubchannelRow(lastLba, track: 0xaa, index: 1);
        WriteMultiplexedRow(multiplexed, lastLba, leadOut);

        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await using var compressor = new CompressionStream(
            output,
            level: 3,
            bufferSize: 1024 * 1024,
            leaveOpen: true);
        await compressor.WriteAsync(multiplexed);
    }

    private static byte[] BuildPositionSubchannelRow(long lba, int track, int index)
    {
        byte[] row = new byte[SubchannelTextService.BytesPerSector];
        Span<byte> q = row.AsSpan(12, 12);
        q[0] = 0x41;
        q[1] = track == 0xaa ? (byte)0xaa : ToBcd(track);
        q[2] = ToBcd(index);
        WriteMsf(q.Slice(3, 3), Math.Max(0, lba));
        q[6] = 0;
        WriteMsf(q.Slice(7, 3), lba + 150);
        ApplyQChannelCrc(q);
        return row;
    }

    private static void WriteMultiplexedRow(byte[] destination, long lba, ReadOnlySpan<byte> row)
    {
        int sectorIndex = checked((int)(lba - SubchannelTextService.RedumperFirstLba));
        Span<byte> column = destination.AsSpan(
            checked(sectorIndex * SubchannelTextService.BytesPerSector),
            SubchannelTextService.BytesPerSector);
        for (int channel = 0; channel < 8; channel++)
        {
            int destinationMask = 0x80 >> channel;
            int rowOffset = channel * 12;
            for (int bit = 0; bit < SubchannelTextService.BytesPerSector; bit++)
            {
                if ((row[rowOffset + bit / 8] & (0x80 >> bit % 8)) != 0)
                    column[bit] |= (byte)destinationMask;
            }
        }
    }

    private static void WriteMsf(Span<byte> target, long frames)
    {
        long minute = frames / (60 * 75);
        long remainder = frames % (60 * 75);
        long second = remainder / 75;
        long frame = remainder % 75;
        target[0] = ToBcd(checked((int)minute));
        target[1] = ToBcd(checked((int)second));
        target[2] = ToBcd(checked((int)frame));
    }

    private static byte ToBcd(int value) => checked((byte)(((value / 10) << 4) | value % 10));

    private static void ApplyQChannelCrc(Span<byte> q)
    {
        ushort crc = 0;
        for (int i = 0; i < 10; i++)
        {
            crc ^= (ushort)(q[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
        }
        crc ^= 0xffff;
        q[10] = (byte)(crc >> 8);
        q[11] = (byte)crc;
    }

    private static byte[] BuildMinimalIso(int imageSectors, int volumeSectors, uint rootLba)
    {
        byte[] image = new byte[imageSectors * 2048];
        Span<byte> pvd = image.AsSpan(16 * 2048, 2048);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "KNOWN_GAP_TEST"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, checked((uint)volumeSectors));
        WriteDirectoryRecord(pvd, 156, rootLba, 2048, 0x02, [0]);

        Span<byte> terminator = image.AsSpan(17 * 2048, 2048);
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        Span<byte> directory = image.AsSpan((int)rootLba * 2048, 2048);
        WriteDirectoryRecord(directory, 0, rootLba, 2048, 0x02, [0]);
        WriteDirectoryRecord(directory, 34, rootLba, 2048, 0x02, [1]);
        return image;
    }

    private static byte[] BuildSplitTrackIso(
        int trackSectors,
        int trackBaseLba,
        uint volumeEndLba,
        uint rootLba,
        uint firstFileLba)
    {
        byte[] image = new byte[trackSectors * 2048];
        Span<byte> pvd = image.AsSpan(16 * 2048, 2048);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "SPLIT_TRACK_TEST"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, volumeEndLba);
        WriteDirectoryRecord(pvd, 156, rootLba, 2048, 0x02, [0]);

        Span<byte> terminator = image.AsSpan(17 * 2048, 2048);
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        int rootIndex = checked((int)rootLba - trackBaseLba);
        Span<byte> directory = image.AsSpan(rootIndex * 2048, 2048);
        int offset = 0;
        offset += WriteDirectoryRecord(directory, offset, rootLba, 2048, 0x02, [0]);
        offset += WriteDirectoryRecord(directory, offset, rootLba, 2048, 0x02, [1]);
        WriteDirectoryRecord(directory, offset, firstFileLba, 1, 0x00, "PAYLOAD.BIN;1"u8);
        image[checked(((int)firstFileLba - trackBaseLba) * 2048)] = 0xA5;
        return image;
    }

    private static void AddToastClassicHfsEvidence(
        byte[] cooked,
        int trackBaseLba,
        uint volumeEndLba)
    {
        Span<byte> pvd = cooked.AsSpan(16 * 2048, 2048);
        pvd.Slice(574, 128).Fill((byte)' ');
        "TOAST ISO 9660 BUILDER COPYRIGHT (C) 1997-2005 SONIC SOLUTIONS - HAVE A NICE DAY"u8
            .CopyTo(pvd[574..]);

        int alternateMdbOffset = checked(((int)volumeEndLba - trackBaseLba - 1) * 2048 + 1024);
        Span<byte> mdb = cooked.AsSpan(alternateMdbOffset, 512);
        BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(18, 2), 667);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.Slice(20, 4), 2048);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(28, 2), 4);
        mdb[36] = 3;
        "GoM"u8.CopyTo(mdb[37..]);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.Slice(84, 4), 19);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.Slice(88, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.Slice(146, 4), 18_432);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(136, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(150, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(152, 2), 9);
    }

    private static byte[] BuildToastClassicSystemArea(
        string partitionName,
        uint partitionMapBlocks,
        uint status)
    {
        const uint partitionStart = 120;
        const uint partitionBlocks = 2676;
        byte[] payload = new byte[16 * 2048];
        Span<byte> sector = payload.AsSpan(0, 2048);
        sector[0] = 0x45;
        sector[1] = 0x52;
        BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(2, 2), 512);
        BinaryPrimitives.WriteUInt32BigEndian(sector.Slice(4, 4), partitionStart + partitionBlocks);
        BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(10, 2), 1);
        WriteApplePartitionEntryForTest(
            sector.Slice(512, 512),
            1,
            partitionMapBlocks,
            "MRKS",
            "Apple_partition_map",
            status);
        WriteApplePartitionEntryForTest(
            sector.Slice(1024, 512),
            partitionStart,
            partitionBlocks,
            partitionName,
            "Apple_HFS",
            status);
        return payload;
    }

    private static void WriteApplePartitionEntryForTest(
        Span<byte> entry,
        uint startBlock,
        uint blockCount,
        string name,
        string type,
        uint status)
    {
        entry[0] = 0x50;
        entry[1] = 0x4D;
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), startBlock);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(12, 4), blockCount);
        Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(32, name.Length)).CopyTo(entry[16..]);
        Encoding.ASCII.GetBytes(type).AsSpan(0, Math.Min(32, type.Length)).CopyTo(entry[48..]);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(84, 4), blockCount);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(88, 4), status);
    }

    private static byte[] ToRawMode1(byte[] cooked, int baseLba = 0)
    {
        int sectors = cooked.Length / 2048;
        byte[] raw = new byte[sectors * 2352];
        for (int lba = 0; lba < sectors; lba++)
        {
            Iso2BinService.BuildRawSectorFromCooked(
                cooked.AsSpan(lba * 2048, 2048),
                raw.AsSpan(lba * 2352, 2352),
                checked(baseLba + lba),
                CdSectorMode.Mode1);
        }
        return raw;
    }

    private static int WriteDirectoryRecord(
        Span<byte> target,
        int offset,
        uint extentLba,
        int dataLength,
        byte flags,
        ReadOnlySpan<byte> identifier)
    {
        int length = 33 + identifier.Length + ((identifier.Length & 1) == 0 ? 1 : 0);
        Span<byte> record = target.Slice(offset, length);
        record.Clear();
        record[0] = checked((byte)length);
        WriteBothEndian32(record, 2, extentLba);
        WriteBothEndian32(record, 10, checked((uint)dataLength));
        record[25] = flags;
        record[28] = 1;
        record[31] = 1;
        record[32] = checked((byte)identifier.Length);
        identifier.CopyTo(record[33..]);
        return length;
    }

    private static void WriteBothEndian32(Span<byte> target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset + 4, 4), value);
    }

    private static string Sha1(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
}
