using System.Buffers.Binary;
using System.Security.Cryptography;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletonSourceImageSpecialMatchingTests
{
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
