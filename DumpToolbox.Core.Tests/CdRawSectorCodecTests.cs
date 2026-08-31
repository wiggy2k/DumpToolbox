namespace DumpToolbox.Core.Tests;

public sealed class CdRawSectorCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(12_345)]
    public void Mode1BuildersRemainByteForByteEquivalent(long lba)
    {
        byte[] payload = BuildPayload(2048);
        byte[] converted = new byte[2352];
        byte[] resurrected = new byte[2352];

        Iso2BinService.BuildRawSectorFromCooked(payload, converted, lba, CdSectorMode.Mode1);
        SkeletonResurrectionService.BuildMode1Sector(lba, payload, resurrected);

        Assert.Equal(converted, resurrected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12_345)]
    public void Mode2Form1BuildersRemainByteForByteEquivalent(long lba)
    {
        byte[] payload = BuildPayload(2048);
        byte[] converted = new byte[2352];
        byte[] resurrected = new byte[2352];

        Iso2BinService.BuildRawSectorFromCooked(payload, converted, lba, CdSectorMode.Mode2Form1);
        SkeletonResurrectionService.BuildMode2Form1Sector(
            lba,
            payload,
            fileNumber: 0,
            channelNumber: 0,
            submode: 0x08,
            codingInfo: 0,
            resurrected);

        Assert.Equal(converted, resurrected);
    }

    [Fact]
    public void DicMode2FaultChangesQParityWithoutChangingStoredPParity()
    {
        byte[] payload = BuildPayload(2048);
        byte[] normal = new byte[2352];
        byte[] faulty = new byte[2352];

        SkeletonResurrectionService.BuildMode2Form1Sector(
            2_000, payload, 0, 0, 0x08, 0, normal);
        SkeletonResurrectionService.BuildMode2Form1Sector(
            2_000, payload, 0, 0, 0x08, 0, faulty, dicLoggedMode2Form1EccError: true);

        Assert.Equal(normal.AsSpan(0, 2248).ToArray(), faulty.AsSpan(0, 2248).ToArray());
        Assert.NotEqual(normal.AsSpan(2248, 104).ToArray(), faulty.AsSpan(2248, 104).ToArray());
    }

    private static byte[] BuildPayload(int length) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 37 + 11)).ToArray();
}
