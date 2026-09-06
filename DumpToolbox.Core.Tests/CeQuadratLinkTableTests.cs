using System.Buffers.Binary;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class CeQuadratLinkTableTests
{
    [Fact]
    public void LargeDirectoryBridgeUsesTheCompleteReservedSectorRange()
    {
        (uint JolietLba, uint PrimaryLba)[] pairs = Enumerable.Range(0, 402)
            .Select(index => (JolietLba: checked((uint)(10_000 + index)), PrimaryLba: checked((uint)(20_000 + index))))
            .ToArray();

        Assert.Null(DicLogImportService.BuildCeQuadratJolietLinkTablePayload(pairs, reservedSectorCount: 1));

        byte[] payload = Assert.IsType<byte[]>(
            DicLogImportService.BuildCeQuadratJolietLinkTablePayload(pairs, reservedSectorCount: 2));
        Assert.Equal(4096, payload.Length);
        Assert.Equal("CeQuadrat Joliet directory link table", Encoding.ASCII.GetString(payload, 0, 37));
        Assert.Equal(402u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(44, 4)));
        int finalPairOffset = 48 + (pairs.Length - 1) * 8;
        Assert.Equal(pairs[^1].JolietLba, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(finalPairOffset, 4)));
        Assert.Equal(pairs[^1].PrimaryLba, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(finalPairOffset + 4, 4)));
        Assert.All(payload[(48 + pairs.Length * 8)..], value => Assert.Equal((byte)0, value));
    }
}
