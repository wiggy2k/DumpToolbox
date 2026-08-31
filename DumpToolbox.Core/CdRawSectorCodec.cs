namespace DumpToolbox.Core;

/// <summary>
/// Shared framing and protection-field primitives for 2352-byte CD-ROM sectors.
/// </summary>
internal static class CdRawSectorCodec
{
    private const int RawSectorSize = 2352;

    private static readonly byte[] SyncBytes =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    private static readonly byte[] EccForward = new byte[256];
    private static readonly byte[] EccBackward = new byte[256];
    private static readonly uint[] EdcTable = new uint[256];

    internal static ReadOnlySpan<byte> SyncPattern => SyncBytes;

    static CdRawSectorCodec()
    {
        for (int i = 0; i < 256; i++)
        {
            int j = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            EccForward[i] = (byte)j;
            EccBackward[i ^ j] = (byte)i;

            uint edc = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0u);
            EdcTable[i] = edc;
        }
    }

    internal static void InitializeSector(Span<byte> sector, long lba, byte mode)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));

        sector.Slice(0, RawSectorSize).Clear();
        SyncPattern.CopyTo(sector);
        WriteSectorAddress(lba, sector);
        sector[15] = mode;
    }

    internal static void WriteSectorAddress(long lba, Span<byte> sector)
    {
        long absolute = checked(lba + 150);
        int minute = (int)(absolute / (75 * 60));
        int second = (int)((absolute / 75) % 60);
        int frame = (int)(absolute % 75);
        sector[12] = ToBcd(minute);
        sector[13] = ToBcd(second);
        sector[14] = ToBcd(frame);
    }

    internal static uint ComputeEdc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        foreach (byte value in data)
            edc = (edc >> 8) ^ EdcTable[(edc ^ value) & 0xFF];
        return edc;
    }

    internal static void GenerateEcc(
        Span<byte> sector,
        bool zeroAddress,
        bool dicLoggedMode2Form1EccError = false)
    {
        Span<byte> savedAddress = stackalloc byte[4];
        if (zeroAddress)
        {
            sector.Slice(12, 4).CopyTo(savedAddress);
            sector.Slice(12, 4).Clear();
        }

        ComputeEccBlock(sector.Slice(12), 86, 24, 2, 86, sector.Slice(2076, 172));

        if (dicLoggedMode2Form1EccError)
        {
            // Reverse-engineered DIC-logged mastering fault: P is stored normally,
            // but Q is calculated as though raw-sector byte 0x873 (one P byte)
            // were zero. Restore the correct stored P byte after generating Q.
            const int faultPByteOffset = 0x873;
            byte correctStoredP = sector[faultPByteOffset];
            sector[faultPByteOffset] = 0x00;
            ComputeEccBlock(sector.Slice(12), 52, 43, 86, 88, sector.Slice(2248, 104));
            sector[faultPByteOffset] = correctStoredP;
        }
        else
        {
            ComputeEccBlock(sector.Slice(12), 52, 43, 86, 88, sector.Slice(2248, 104));
        }

        if (zeroAddress)
            savedAddress.CopyTo(sector.Slice(12, 4));
    }

    private static byte ToBcd(int value)
    {
        if ((uint)value > 99)
            throw new ArgumentOutOfRangeException(nameof(value), "BCD value must be between 0 and 99.");
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    private static void ComputeEccBlock(
        ReadOnlySpan<byte> source,
        int majorCount,
        int minorCount,
        int majorMult,
        int minorInc,
        Span<byte> destination)
    {
        int size = majorCount * minorCount;
        for (int major = 0; major < majorCount; major++)
        {
            int index = (major >> 1) * majorMult + (major & 1);
            byte eccA = 0;
            byte eccB = 0;

            for (int minor = 0; minor < minorCount; minor++)
            {
                byte temp = source[index];
                index += minorInc;
                if (index >= size)
                    index -= size;
                eccA ^= temp;
                eccB ^= temp;
                eccA = EccForward[eccA];
            }

            eccA = EccBackward[EccForward[eccA] ^ eccB];
            destination[major] = eccA;
            destination[major + majorCount] = (byte)(eccA ^ eccB);
        }
    }
}
