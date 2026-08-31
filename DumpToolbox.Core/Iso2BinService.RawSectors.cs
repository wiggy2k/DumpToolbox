using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class Iso2BinService
{
    private static bool IsIso9660Descriptor(byte[] sector) =>
        sector.AsSpan(1, 5).SequenceEqual(Cd001);

    private static bool HasXaMarker(byte[] sector) =>
        sector.AsSpan(1024, 8).SequenceEqual(XaMarker);

    private static void BuildBatch(
        byte[] inputBuffer,
        byte[] outputBuffer,
        int sectorCount,
        long firstLba,
        CdSectorMode mode,
        XaMetadataMap? xaMetadata = null,
        XaMetadataUsage? xaUsage = null)
    {
        for (int i = 0; i < sectorCount; i++)
        {
            long lba = firstLba + i;
            ReadOnlySpan<byte> userData = inputBuffer.AsSpan(i * CookedSectorSize, CookedSectorSize);
            Span<byte> rawSector = outputBuffer.AsSpan(i * RawSectorSize, RawSectorSize);

            XaSectorMetadata? metadata = null;
            if (mode == CdSectorMode.Mode2Form1 && xaMetadata is not null)
            {
                if (xaMetadata.TryGet(lba, out XaSectorMetadata found))
                {
                    if (found.Mode == 2 && found.Form == 2)
                    {
                        throw new InvalidOperationException(
                            $"XA metadata marks LBA {lba:N0} as Mode 2 Form 2. A 2048-byte ISO/CUE source cannot reconstruct that 2324-byte Form 2 payload.");
                    }
                    if (found.Mode != 2 || found.Form != 1)
                    {
                        throw new InvalidOperationException(
                            $"XA metadata marks LBA {lba:N0} as Mode {found.Mode}, but the cooked source is being expanded as Mode 2 Form 1. Check that the metadata belongs to this disc/layout.");
                    }

                    metadata = found;
                    if (xaUsage is not null)
                        xaUsage.ExactSubheaders++;
                }
                else if (xaUsage is not null)
                {
                    xaUsage.GenericSubheaders++;
                }
            }

            BuildRawSector(userData, rawSector, lba, mode, metadata);
        }
    }

    internal static void BuildRawSectorFromCooked(
        ReadOnlySpan<byte> userData,
        Span<byte> sector,
        long lba,
        CdSectorMode mode)
    {
        BuildRawSector(userData, sector, lba, mode);
    }

    private static void BuildRawSector(
        ReadOnlySpan<byte> userData,
        Span<byte> sector,
        long lba,
        CdSectorMode mode,
        XaSectorMetadata? xaMetadata = null)
    {
        sector.Clear();
        SyncPattern.AsSpan().CopyTo(sector);
        WriteMsfHeader(sector, lba, mode);

        if (mode == CdSectorMode.Mode1)
        {
            userData.CopyTo(sector.Slice(16, CookedSectorSize));
            uint edc = ComputeEdc(sector.Slice(0, 2064));
            BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2064, 4), edc);
            GenerateEcc(sector, zeroAddress: false);
        }
        else
        {
            XaSectorMetadata metadata = xaMetadata ?? XaSectorMetadata.GenericForm1;
            sector[16] = metadata.FileNumber;
            sector[17] = metadata.ChannelNumber;
            sector[18] = metadata.Submode;
            sector[19] = metadata.CodingInfo;
            sector.Slice(16, 4).CopyTo(sector.Slice(20, 4));
            userData.CopyTo(sector.Slice(24, CookedSectorSize));

            uint edc = ComputeEdc(sector.Slice(16, 2056));
            BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2072, 4), edc);
            GenerateEcc(sector, zeroAddress: true);
        }
    }

    private static void WriteMsfHeader(Span<byte> sector, long lba, CdSectorMode mode)
    {
        long absoluteFrame = lba + 150;
        int minute = (int)(absoluteFrame / (75 * 60));
        int second = (int)((absoluteFrame / 75) % 60);
        int frame = (int)(absoluteFrame % 75);

        sector[12] = ToBcd(minute);
        sector[13] = ToBcd(second);
        sector[14] = ToBcd(frame);
        sector[15] = mode == CdSectorMode.Mode1 ? (byte)0x01 : (byte)0x02;
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static uint ComputeEdc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        foreach (byte value in data)
            edc = (edc >> 8) ^ EdcTable[(edc ^ value) & 0xFF];
        return edc;
    }

    private static void GenerateEcc(Span<byte> sector, bool zeroAddress)
    {
        Span<byte> savedAddress = stackalloc byte[4];
        if (zeroAddress)
        {
            sector.Slice(12, 4).CopyTo(savedAddress);
            sector.Slice(12, 4).Clear();
        }

        ComputeEccBlock(sector.Slice(12), 86, 24, 2, 86, sector.Slice(2076, 172));
        ComputeEccBlock(sector.Slice(12), 52, 43, 86, 88, sector.Slice(2248, 104));

        if (zeroAddress)
            savedAddress.CopyTo(sector.Slice(12, 4));
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
