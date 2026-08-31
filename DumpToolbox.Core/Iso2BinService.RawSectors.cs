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
        CdRawSectorCodec.InitializeSector(
            sector,
            lba,
            mode == CdSectorMode.Mode1 ? (byte)0x01 : (byte)0x02);

        if (mode == CdSectorMode.Mode1)
        {
            userData.CopyTo(sector.Slice(16, CookedSectorSize));
            uint edc = CdRawSectorCodec.ComputeEdc(sector.Slice(0, 2064));
            BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2064, 4), edc);
            CdRawSectorCodec.GenerateEcc(sector, zeroAddress: false);
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

            uint edc = CdRawSectorCodec.ComputeEdc(sector.Slice(16, 2056));
            BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2072, 4), edc);
            CdRawSectorCodec.GenerateEcc(sector, zeroAddress: true);
        }
    }
}
