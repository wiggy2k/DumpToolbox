using System.Buffers.Binary;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class CeQuadratFooterTests
{
    [Fact]
    public void TextPayloadCarriesBothEndianLbaAndFixedMarker()
    {
        const long lba = 123_456;
        byte[] payload = CeQuadratFooterCodec.BuildTextPayload(lba);

        Assert.Equal(2048, payload.Length);
        Assert.Equal(
            "CeQuadrat ISO 9660 formatter information block",
            Encoding.ASCII.GetString(payload, 0, CeQuadratFooterCodec.TextSignature.Length));
        Assert.Equal((uint)lba, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x80, 4)));
        Assert.Equal((uint)lba, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0x84, 4)));
        Assert.Equal(new byte[] { 0xaa, 0x55, 0x55, 0xaa }, payload[0x7fc..]);
        Assert.True(CeQuadratFooterCodec.IsExactTextPayload(payload, lba));

        payload[100] = 1;
        Assert.False(CeQuadratFooterCodec.IsExactTextPayload(payload, lba));
    }

    [Fact]
    public void BasicBinaryPayloadUsesKnownFieldsAndChecksum()
        => AssertBinaryPayload(CeQuadratBinaryFooterVariant.Basic, 0x01f00000u, 0u, 0u);

    [Fact]
    public void ExtendedBinaryPayloadUsesKnownFieldsAndChecksum()
        => AssertBinaryPayload(CeQuadratBinaryFooterVariant.Extended, 0x01f0de35u, 1u, 0x004470f6u);

    private static void AssertBinaryPayload(
        CeQuadratBinaryFooterVariant variant,
        uint variantWord,
        uint wordAt10,
        uint wordAt14)
    {
        const long lba = 654_321;
        byte[] payload = CeQuadratFooterCodec.BuildBinaryPayload(lba, variant);

        Assert.Equal(0x00020002u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x00, 4)));
        Assert.Equal(variantWord, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x08, 4)));
        Assert.Equal((uint)lba, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x0c, 4)));
        Assert.Equal(wordAt10, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x10, 4)));
        Assert.Equal(wordAt14, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x14, 4)));
        int expectedChecksum = payload[..4].Sum(value => value) + payload[8..16].Sum(value => value);
        Assert.Equal(unchecked((byte)expectedChecksum), payload[4]);
        Assert.All(payload[0x18..], value => Assert.Equal((byte)0, value));
        Assert.True(CeQuadratFooterCodec.TryClassifyExactBinaryPayload(payload, lba, out CeQuadratBinaryFooterVariant actual));
        Assert.Equal(variant, actual);

        payload[0x400] = 1;
        Assert.False(CeQuadratFooterCodec.TryClassifyExactBinaryPayload(payload, lba, out _));
    }

    [Fact]
    public void LayoutsPlaceTextAndBinaryAtTheObservedVolumeOffsets()
    {
        const long penultimate = 9998;
        const long final = 9999;
        (byte[] first, byte[] second) = DicDonorImageService.BuildCeQuadratFooterLayout(
            DicCeQuadratFooterLayout.TextAtPenultimateWithExtendedBinaryFinal,
            penultimate,
            final);

        Assert.True(CeQuadratFooterCodec.IsExactTextPayload(first, penultimate));
        Assert.True(CeQuadratFooterCodec.TryClassifyExactBinaryPayload(
            second,
            final,
            out CeQuadratBinaryFooterVariant variant));
        Assert.Equal(CeQuadratBinaryFooterVariant.Extended, variant);
    }

    [Fact]
    public void DonorLayoutAtDifferentLbasCanBeIdentifiedAndRegeneratedForTarget()
    {
        (byte[] donorPenultimate, byte[] donorFinal) = DicDonorImageService.BuildCeQuadratFooterLayout(
            DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal,
            4998,
            4999);
        DicCeQuadratFooterLayout? identified = DicDonorImageService.IdentifyCeQuadratFooterLayout(
            donorPenultimate,
            donorFinal,
            4998,
            4999);

        Assert.Equal(DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal, identified);
        (byte[] targetPenultimate, byte[] targetFinal) = DicDonorImageService.BuildCeQuadratFooterLayout(
            identified!.Value,
            8998,
            8999);
        Assert.True(CeQuadratFooterCodec.IsExactTextPayload(targetPenultimate, 8998));
        Assert.True(CeQuadratFooterCodec.TryClassifyExactBinaryPayload(
            targetFinal,
            8999,
            out CeQuadratBinaryFooterVariant variant));
        Assert.Equal(CeQuadratBinaryFooterVariant.Basic, variant);
    }

    [Fact]
    public void TextAtFinalIsRecognisedWhenPenultimateSectorBelongsToAFile()
    {
        byte[] occupiedPenultimate = Enumerable.Repeat((byte)0x5a, 2048).ToArray();
        byte[] final = CeQuadratFooterCodec.BuildTextPayload(7000);

        DicCeQuadratFooterLayout? identified = DicDonorImageService.IdentifyCeQuadratFooterLayout(
            occupiedPenultimate,
            final,
            6999,
            7000);

        Assert.Equal(DicCeQuadratFooterLayout.TextAtFinalVolumeSector, identified);
    }
}
