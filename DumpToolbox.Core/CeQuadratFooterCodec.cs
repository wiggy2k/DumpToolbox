using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

internal enum CeQuadratBinaryFooterVariant
{
    Basic,
    Extended
}

internal static class CeQuadratFooterCodec
{
    internal const int PayloadSize = 2048;
    internal static readonly byte[] TextSignature =
        Encoding.ASCII.GetBytes("CeQuadrat ISO 9660 formatter information block");

    internal static byte[] BuildTextPayload(long lba)
    {
        uint address = checked((uint)lba);
        var payload = new byte[PayloadSize];
        TextSignature.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x80, 4), address);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x84, 4), address);
        payload[0x7fc] = 0xaa;
        payload[0x7fd] = 0x55;
        payload[0x7fe] = 0x55;
        payload[0x7ff] = 0xaa;
        return payload;
    }

    internal static byte[] BuildBinaryPayload(long lba, CeQuadratBinaryFooterVariant variant)
    {
        uint address = checked((uint)lba);
        var payload = new byte[PayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x00, 4), 0x00020002);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x08, 4),
            variant == CeQuadratBinaryFooterVariant.Extended ? 0x01f0de35u : 0x01f00000u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x0c, 4), address);
        if (variant == CeQuadratBinaryFooterVariant.Extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14, 4), 0x004470f6);
        }

        int checksum = 0;
        for (int offset = 0; offset < 4; offset++)
            checksum += payload[offset];
        for (int offset = 8; offset < 16; offset++)
            checksum += payload[offset];
        payload[0x04] = unchecked((byte)checksum);
        return payload;
    }

    internal static bool IsExactTextPayload(ReadOnlySpan<byte> payload, long lba)
        => lba >= 0 && lba <= uint.MaxValue &&
           payload.SequenceEqual(BuildTextPayload(lba));

    internal static bool TryClassifyExactBinaryPayload(
        ReadOnlySpan<byte> payload,
        long lba,
        out CeQuadratBinaryFooterVariant variant)
    {
        variant = default;
        if (lba < 0 || lba > uint.MaxValue || payload.Length != PayloadSize)
            return false;

        if (payload.SequenceEqual(BuildBinaryPayload(lba, CeQuadratBinaryFooterVariant.Basic)))
        {
            variant = CeQuadratBinaryFooterVariant.Basic;
            return true;
        }
        if (payload.SequenceEqual(BuildBinaryPayload(lba, CeQuadratBinaryFooterVariant.Extended)))
        {
            variant = CeQuadratBinaryFooterVariant.Extended;
            return true;
        }
        return false;
    }
}
