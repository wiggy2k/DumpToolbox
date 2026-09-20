using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class KnownSystemAreaRecoveryTests
{
    [Fact]
    public void BuildsAllSectorRomMakerAndDummyTemplates()
    {
        byte[] romMaker = SkeletonResurrectionService.BuildRepeatedTextSystemArea(
            "The Personal RomMaker for the Macintosh, Version 1.02",
            firstSector: 0);
        byte[] dummy = SkeletonResurrectionService.BuildRepeatedDummyAppleSystemArea(firstSector: 0);

        Assert.Equal("2931072db3c3794c569aaf6eaf9102edefe1bb78", Sha1(romMaker));
        Assert.Equal("35993156b028a93ca8b9dd7f9ffd39dcb43f7b4f", Sha1(dummy));
    }

    [Fact]
    public void BuildsKnownWindowsRomMakerLayout()
    {
        byte[] payload = SkeletonResurrectionService.BuildRepeatedTextSystemArea(
            "The Personal RomMaker (Windows V3.01)",
            firstSector: 1);

        Assert.Equal("ef499d03322a6858e10fe2eaca3f04be79f944ed", Sha1(payload));
    }

    [Theory]
    [InlineData("65332febdf8b70b510afdcb3b54b9e095a953c6b", 72_191u, 105u)]
    [InlineData("4f38944d4e5d64ca73c6320d6f0244f1ad9e4451", 130_940u, 222u)]
    [InlineData("c4d11910188083182d190a006dcd8069867264b2", 319_592u, 229u)]
    public void ReconstructsKnownApplePartitionMapFamilies(
        string expectedSha1,
        uint volumeSpaceSize,
        uint hfsStart)
    {
        KnownSystemAreaRecoveryInfo? result =
            SkeletonResurrectionService.TryBuildKnownAppleSystemAreaForGeometry(
                expectedSha1,
                volumeSpaceSize,
                [hfsStart]);

        Assert.NotNull(result);
        Assert.Equal(expectedSha1, Sha1(result.Payload));
    }

    [Fact]
    public void BuildsKnownRepeatedRivenFfLayout()
    {
        byte[] payload = SkeletonResurrectionService.BuildKnownRivenFfSystemArea();

        Assert.Equal("b9aec682b6a6e7101af5253f4fdcf1134af0b57b", Sha1(payload));
        for (int lba = 5; lba <= 7; lba++)
            Assert.All(payload.AsSpan(lba * 2048, 2048).ToArray(), value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public void ProducesGeneratedKnownSystemAreaMatch()
    {
        byte[] payload = SkeletonResurrectionService.BuildRepeatedTextSystemArea(
            "The Personal RomMaker (Windows V2.01)",
            firstSector: 1);
        string expectedSha1 = Sha1(payload);
        var systemArea = new SkeletonContentEntry(
            "SYSTEM_AREA",
            0,
            16 * 2048,
            expectedSha1,
            null,
            SkeletonSpecialKind.SystemArea);
        var inspection = new SkeletonInspectionResult(
            "disc.skeleton",
            "disc.hash",
            SkeletonImageKind.Cooked2048,
            2048,
            0,
            16,
            [systemArea],
            "TEST",
            1,
            0)
        {
            KnownSystemAreaRecovery = new KnownSystemAreaRecoveryInfo(
                "Personal RomMaker test",
                expectedSha1,
                payload)
        };

        SkeletonSourceMatch match = SkeletonResurrectionService.RecoverKnownSystemArea(inspection);

        Assert.Equal("Known repeatable system-area reconstruction", match.MatchMethod);
        Assert.Equal(expectedSha1, match.Sha1);
        Assert.Equal(payload, match.GeneratedPayload);
    }

    [Fact]
    public async Task InspectionFindsHfsGeometryAndResurrectionUsesGeneratedSystemArea()
    {
        const uint volumeSectors = 200;
        const uint hfsStart = 100;
        const uint hfsEnd = volumeSectors * 4 - 3;
        const uint ddmBlocks = hfsEnd + 1_200;
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"known-system-area-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string skeletonPath = Path.Combine(tempDirectory, "disc.skeleton");
        string hashPath = Path.Combine(tempDirectory, "disc.hash");
        string outputPath = Path.Combine(tempDirectory, "disc.iso");

        try
        {
            byte[] image = BuildMinimalHybridSkeleton(volumeSectors, hfsStart);
            File.WriteAllBytes(skeletonPath, image);
            byte[] expectedSystemArea = SkeletonResurrectionService.BuildThreeEntryAppleSystemArea(
                "Dummy Apple Volume",
                ddmBlocks,
                hfsStart,
                hfsEnd - hfsStart,
                includeMrks: false);
            string expectedSha1 = Sha1(expectedSystemArea);
            File.WriteAllText(hashPath, $"{expectedSha1} SYSTEM_AREA{Environment.NewLine}");

            SkeletonInspectionResult inspection = await new SkeletonResurrectionService()
                .InspectAsync(skeletonPath, hashPath);

            Assert.NotNull(inspection.KnownSystemAreaRecovery);
            Assert.Contains("Dummy Apple Volume", inspection.KnownSystemAreaRecovery.PatternName);
            Assert.True(SkeletonResurrectionService.CanRecoverKnownSystemArea(inspection));

            SkeletonResurrectionResult result = await new SkeletonResurrectionService().ResurrectAsync(
                inspection,
                new Dictionary<string, SkeletonSourceMatch>(),
                outputPath,
                allowMissing: false);

            Assert.Equal(0, result.MissingEntries);
            byte[] resurrected = File.ReadAllBytes(outputPath);
            Assert.Equal(expectedSystemArea, resurrected.AsSpan(0, 16 * 2048).ToArray());
            Assert.Equal(expectedSha1, Sha1(resurrected.AsSpan(0, 16 * 2048).ToArray()));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] BuildMinimalHybridSkeleton(uint volumeSectors, uint hfsStart)
    {
        const int sectorSize = 2048;
        const uint rootLba = 20;
        byte[] image = new byte[checked((int)volumeSectors * sectorSize)];

        Span<byte> pvd = image.AsSpan(16 * sectorSize, sectorSize);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "HYBRID_TEST"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, volumeSectors);
        WriteDirectoryRecord(pvd, 156, rootLba, sectorSize, 0x02, [0]);

        Span<byte> terminator = image.AsSpan(17 * sectorSize, sectorSize);
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        Span<byte> directory = image.AsSpan((int)rootLba * sectorSize, sectorSize);
        WriteDirectoryRecord(directory, 0, rootLba, sectorSize, 0x02, [0]);
        WriteDirectoryRecord(directory, 34, rootLba, sectorSize, 0x02, [1]);

        int mdbOffset = checked((int)(hfsStart + 2) * 512);
        Span<byte> mdb = image.AsSpan(mdbOffset, 512);
        mdb[0] = 0x42;
        mdb[1] = 0x44;
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(18, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.Slice(20, 4), 512);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.Slice(28, 2), 4);
        mdb[36] = 4;
        "TEST"u8.CopyTo(mdb[37..]);
        return image;
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

    private static string Sha1(byte[] payload) =>
        Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
}
