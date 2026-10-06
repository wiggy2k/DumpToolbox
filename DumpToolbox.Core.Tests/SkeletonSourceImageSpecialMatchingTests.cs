using System.Buffers.Binary;
using System.Security.Cryptography;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletonSourceImageSpecialMatchingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShortDonorGapUsesZerosOnlyWhenHashMatches(bool raw, bool wrongHash)
    {
        string root = Path.Combine(Path.GetTempPath(), $"gap-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            byte[] donor = BuildMinimalIso(24, 20);
            donor.AsSpan(22 * 2048, 2 * 2048).Fill(0xA5);
            byte[] expected = new byte[26 * 2048];
            donor.CopyTo(expected, 0);
            string source = Path.Combine(root, raw ? "donor.bin" : "donor.iso");
            string skeleton = Path.Combine(root, "disc.skeleton");
            byte[] erased = (byte[])expected.Clone();
            erased.AsSpan(22 * 2048).Clear();
            await File.WriteAllBytesAsync(source, raw ? ToRawMode1(donor) : donor);
            await File.WriteAllBytesAsync(skeleton, ToRawMode1(erased));
            var entry = new SkeletonContentEntry("GAP_0000022", 22, 4 * 2048,
                wrongHash ? new string('1', 40) : Sha1(expected.AsSpan(22 * 2048)), null,
                SkeletonSpecialKind.Gap, RequiresSource: true);
            var inspection = new SkeletonInspectionResult(skeleton, Path.Combine(root, "disc.hash"),
                SkeletonImageKind.Raw2352, 2352, 0, 26, [entry], "SPECIAL_TEST", 1, 0);
            var service = new SkeletonResurrectionService();
            var matches = await service.MatchSourceImageAsync(inspection, source, useHistoryDatabase: false);
            if (wrongHash)
            {
                Assert.Empty(matches);
                return;
            }
            var match = Assert.Single(matches).Value;
            Assert.Equal(4096, match.SourceZeroPaddingBytes);
            Assert.Equal(8192, match.SourceLength);
            Assert.Equal(4096, Assert.Single(match.SourceImageExtents!).Length);
            using (var stream = new OpticalImageExtentStream(source, match.SourceImageExtents!, 8192, 4096))
            {
                stream.Seek(4090, SeekOrigin.Begin);
                byte[] boundary = new byte[20];
                stream.ReadExactly(boundary);
                Assert.All(boundary[..6], value => Assert.Equal(0xA5, value));
                Assert.All(boundary[6..], value => Assert.Equal(0, value));
            }
            string output = Path.Combine(root, "output.bin");
            await service.ResurrectAsync(inspection, matches, output, allowMissing: false);
            Assert.Equal(expected, ReadRawUserData(await File.ReadAllBytesAsync(output), 0, 26));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceImageMatchesToast25SystemAreaMrksMarkerVariant(bool sourceHasMarker)
    {
        const int volumeSectors = 40;
        const uint rootLba = 20;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-toast25-system-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "disc.skeleton");
        string sourcePath = Path.Combine(root, "alternate.bin");
        string outputPath = Path.Combine(root, "resurrected.bin");

        try
        {
            byte[] sourceCooked = BuildMinimalIso(volumeSectors, rootLba);
            byte[] sourceSystemArea = BuildToast25SystemArea(sourceHasMarker);
            sourceSystemArea.CopyTo(sourceCooked, 0);

            byte[] targetSystemArea = (byte[])sourceSystemArea.Clone();
            if (sourceHasMarker)
                targetSystemArea.AsSpan(1020, 4).Clear();
            else
                "MRKS"u8.CopyTo(targetSystemArea.AsSpan(1020, 4));

            byte[] skeletonCooked = (byte[])sourceCooked.Clone();
            skeletonCooked.AsSpan(0, 16 * 2048).Clear();
            await File.WriteAllBytesAsync(sourcePath, ToRawMode1(sourceCooked));
            await File.WriteAllBytesAsync(skeletonPath, ToRawMode1(skeletonCooked));

            var entry = new SkeletonContentEntry(
                "SYSTEM_AREA",
                0,
                16 * 2048,
                Sha1(targetSystemArea),
                null,
                SkeletonSpecialKind.SystemArea,
                RequiresSource: true);
            var inspection = new SkeletonInspectionResult(
                skeletonPath,
                Path.Combine(root, "disc.hash"),
                SkeletonImageKind.Raw2352,
                2352,
                0,
                volumeSectors,
                [entry],
                "TOAST25_TEST",
                1,
                0);

            var service = new SkeletonResurrectionService();
            IReadOnlyDictionary<string, SkeletonSourceMatch> matches =
                await service.MatchSourceImageAsync(inspection, sourcePath, useHistoryDatabase: false);

            SkeletonSourceMatch match = Assert.Single(matches).Value;
            Assert.NotNull(match.GeneratedPayload);
            Assert.Equal(targetSystemArea, match.GeneratedPayload);
            Assert.Contains("Toast 2.5", match.MatchMethod);

            SkeletonResurrectionResult result = await service.ResurrectAsync(
                inspection,
                matches,
                outputPath,
                allowMissing: false);

            Assert.Equal(0, result.MissingEntries);
            byte[] output = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(targetSystemArea, ReadRawUserData(output, 0, 16));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SourceImageMatchesAndRestoresSystemAreaAndGapRegions()
    {
        const int volumeSectors = 40;
        const uint rootLba = 20;
        const uint gapLba = 21;
        const int gapSectors = 2;
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-image-specials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string skeletonPath = Path.Combine(root, "disc.skeleton");
        string sourcePath = Path.Combine(root, "alternate.bin");
        string outputPath = Path.Combine(root, "resurrected.bin");

        try
        {
            byte[] sourceCooked = BuildMinimalIso(volumeSectors, rootLba);
            for (int lba = 0; lba < 16; lba++)
                sourceCooked.AsSpan(lba * 2048, 2048).Fill(checked((byte)(0x40 + lba)));
            sourceCooked.AsSpan((int)gapLba * 2048, 2048).Fill(0xA5);
            sourceCooked.AsSpan(((int)gapLba + 1) * 2048, 2048).Fill(0x5A);

            byte[] skeletonCooked = (byte[])sourceCooked.Clone();
            skeletonCooked.AsSpan(0, 16 * 2048).Clear();
            skeletonCooked.AsSpan((int)gapLba * 2048, gapSectors * 2048).Clear();
            await File.WriteAllBytesAsync(sourcePath, ToRawMode1(sourceCooked));
            await File.WriteAllBytesAsync(skeletonPath, ToRawMode1(skeletonCooked));

            string systemSha1 = Sha1(sourceCooked.AsSpan(0, 16 * 2048));
            string gapSha1 = Sha1(sourceCooked.AsSpan((int)gapLba * 2048, gapSectors * 2048));
            SkeletonContentEntry[] entries =
            [
                new("SYSTEM_AREA", 0, 16 * 2048, systemSha1, null,
                    SkeletonSpecialKind.SystemArea, RequiresSource: true),
                new("GAP_0000021", gapLba, gapSectors * 2048, gapSha1, null,
                    SkeletonSpecialKind.Gap, RequiresSource: true)
            ];
            var inspection = new SkeletonInspectionResult(
                skeletonPath,
                Path.Combine(root, "disc.hash"),
                SkeletonImageKind.Raw2352,
                2352,
                0,
                volumeSectors,
                entries,
                "SPECIAL_TEST",
                entries.Length,
                0);

            var service = new SkeletonResurrectionService();
            IReadOnlyDictionary<string, SkeletonSourceMatch> matches =
                await service.MatchSourceImageAsync(inspection, sourcePath, useHistoryDatabase: false);

            Assert.Equal(2, matches.Count);
            SkeletonSourceMatch systemMatch = matches["SYSTEM_AREA"];
            Assert.Equal(0, systemMatch.SourceImageLba);
            Assert.Equal(16 * 2048, systemMatch.SourceLength);
            Assert.Contains("SYSTEM_AREA", systemMatch.MatchMethod);
            SkeletonSourceMatch gapMatch = matches["GAP_0000021"];
            Assert.Equal(gapLba, gapMatch.SourceImageLba);
            Assert.Equal(gapSectors * 2048, gapMatch.SourceLength);
            Assert.Contains("GAP", gapMatch.MatchMethod);

            SkeletonResurrectionResult result = await service.ResurrectAsync(
                inspection,
                matches,
                outputPath,
                allowMissing: false);

            Assert.Equal(0, result.MissingEntries);
            byte[] output = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(
                sourceCooked.AsSpan(0, 16 * 2048).ToArray(),
                ReadRawUserData(output, 0, 16));
            Assert.Equal(
                sourceCooked.AsSpan((int)gapLba * 2048, gapSectors * 2048).ToArray(),
                ReadRawUserData(output, gapLba, gapSectors));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static byte[] BuildMinimalIso(int sectors, uint rootLba)
    {
        byte[] image = new byte[sectors * 2048];
        Span<byte> pvd = image.AsSpan(16 * 2048, 2048);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "SPECIAL_TEST"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, checked((uint)sectors));
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

    private static byte[] BuildToast25SystemArea(bool includeMrksMarker)
    {
        byte[] payload = new byte[16 * 2048];
        Span<byte> descriptor = payload.AsSpan(0, 512);
        "ER"u8.CopyTo(descriptor);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.Slice(2, 2), 512);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.Slice(4, 4), 30);

        Span<byte> partitionMap = payload.AsSpan(512, 512);
        WriteApplePartitionEntry(partitionMap, 2, 1, 2, "MRKS", "Apple_partition_map");
        if (includeMrksMarker)
            "MRKS"u8.CopyTo(partitionMap.Slice(508, 4));

        Span<byte> hfsPartition = payload.AsSpan(1024, 512);
        WriteApplePartitionEntry(hfsPartition, 2, 20, 8, "TOAST 2.5 Partition", "Apple_HFS");
        return payload;
    }

    private static void WriteApplePartitionEntry(
        Span<byte> entry,
        uint mapEntries,
        uint startBlock,
        uint blockCount,
        string name,
        string type)
    {
        "PM"u8.CopyTo(entry);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4, 4), mapEntries);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), startBlock);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(12, 4), blockCount);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(entry.Slice(16, 32));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(entry.Slice(48, 32));
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(84, 4), blockCount);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(88, 4), 0x13);
    }

    private static byte[] ToRawMode1(byte[] cooked)
    {
        int sectors = cooked.Length / 2048;
        byte[] raw = new byte[sectors * 2352];
        for (int lba = 0; lba < sectors; lba++)
        {
            Iso2BinService.BuildRawSectorFromCooked(
                cooked.AsSpan(lba * 2048, 2048),
                raw.AsSpan(lba * 2352, 2352),
                lba,
                CdSectorMode.Mode1);
        }
        return raw;
    }

    private static byte[] ReadRawUserData(byte[] raw, long startLba, int sectorCount)
    {
        byte[] result = new byte[sectorCount * 2048];
        for (int i = 0; i < sectorCount; i++)
        {
            raw.AsSpan(checked((int)((startLba + i) * 2352 + 16)), 2048)
                .CopyTo(result.AsSpan(i * 2048, 2048));
        }
        return result;
    }

    private static void WriteDirectoryRecord(
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
    }

    private static void WriteBothEndian32(Span<byte> target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset + 4, 4), value);
    }

    private static string Sha1(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
}
