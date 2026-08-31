using System.Buffers.Binary;

namespace DumpToolbox.Core;

/// <summary>
/// Raw 2352-byte CD sector classification, framing, EDC and ECC helpers.
/// </summary>
public sealed partial class SkeletonResurrectionService
{
    private static RawSectorPayloadKind GetRawPayloadKind(byte[] sector)
    {
        return GetRawPayloadKind(sector.AsSpan(0, RawSectorSize));
    }

    private static void RebuildErrorFields(
        byte[] sector,
        RawSectorPayloadKind kind,
        bool mode2Form2NoEdc = false,
        bool dicLoggedMode2Form1EccError = false)
    {
        RebuildErrorFields(sector.AsSpan(0, RawSectorSize), kind, mode2Form2NoEdc, dicLoggedMode2Form1EccError);
    }

    private static RawSectorPayloadKind GetRawPayloadKind(ReadOnlySpan<byte> sector)
    {
        // DIC/EccEdc can report Mode 1/2 sectors whose upper mode-byte bits are
        // deliberately set ("Block Indicators" / invalid-mode protection). The
        // low two bits still identify the logical CD-ROM mode and must be used for
        // payload placement while preserving the full raw byte in the sector.
        byte logicalMode = (byte)(sector[15] & 0x03);
        if (logicalMode == 1)
            return RawSectorPayloadKind.Mode1;
        if (logicalMode == 2)
            return (sector[18] & XaForm2Bit) != 0
                ? RawSectorPayloadKind.Mode2Form2
                : RawSectorPayloadKind.Mode2Form1;
        return RawSectorPayloadKind.Unsupported;
    }

    /// <summary>
    /// Rebuilds the protection fields of an existing Mode 2 Form 1 raw sector in place.
    /// This preserves sync/header/XA subheader/user data and is used after a raw same-disc
    /// donor sector has been copied into a DIC reconstruction.
    /// </summary>
    internal static void RebuildMode2Form1ProtectionFields(
        Span<byte> sector,
        bool dicLoggedMode2Form1EccError = false)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));
        if ((sector[15] & 0x03) != 2 || (sector[18] & XaForm2Bit) != 0)
            throw new InvalidOperationException("The raw sector is not Mode 2 Form 1.");

        RebuildErrorFields(
            sector.Slice(0, RawSectorSize),
            RawSectorPayloadKind.Mode2Form1,
            mode2Form2NoEdc: false,
            dicLoggedMode2Form1EccError: dicLoggedMode2Form1EccError);
    }

    private static void RebuildErrorFields(
        Span<byte> sector,
        RawSectorPayloadKind kind,
        bool mode2Form2NoEdc = false,
        bool dicLoggedMode2Form1EccError = false)
    {
        switch (kind)
        {
            case RawSectorPayloadKind.Mode1:
            {
                uint edc = CdRawSectorCodec.ComputeEdc(sector.Slice(0, 2064));
                BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2064, 4), edc);
                CdRawSectorCodec.GenerateEcc(sector, zeroAddress: false);
                break;
            }
            case RawSectorPayloadKind.Mode2Form1:
            {
                uint edc = CdRawSectorCodec.ComputeEdc(sector.Slice(16, 2056));
                BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2072, 4), edc);
                CdRawSectorCodec.GenerateEcc(sector, zeroAddress: true, dicLoggedMode2Form1EccError: dicLoggedMode2Form1EccError);
                break;
            }
            case RawSectorPayloadKind.Mode2Form2:
            {
                if (mode2Form2NoEdc)
                {
                    // XA Mode 2 Form 2 permits the optional EDC field to be absent.
                    // DiscImageCreator reports these sectors as "mode 2 no edc"; in
                    // that case the final four bytes must remain zero and there is no ECC.
                    sector.Slice(2348, 4).Clear();
                }
                else
                {
                    uint edc = CdRawSectorCodec.ComputeEdc(sector.Slice(16, 2332));
                    BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(2348, 4), edc);
                }
                break;
            }
            default:
                throw new NotSupportedException("Unsupported raw CD sector mode.");
        }
    }

    internal static void BuildMode1Sector(
        long lba,
        ReadOnlySpan<byte> userData,
        Span<byte> sector,
        byte[]? rawHeaderOverride = null)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));
        if (userData.Length != CookedSectorSize)
            throw new ArgumentException("Mode 1 user data must be exactly 2048 bytes.", nameof(userData));

        CdRawSectorCodec.InitializeSector(sector, lba, mode: 1);
        ApplyRawHeaderOverride(sector, rawHeaderOverride);
        userData.CopyTo(sector.Slice(16, CookedSectorSize));
        RebuildErrorFields(sector, RawSectorPayloadKind.Mode1);
    }

    internal static void BuildMode0Sector(
        long lba,
        Span<byte> sector,
        byte[]? rawHeaderOverride = null)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));

        CdRawSectorCodec.InitializeSector(sector, lba, mode: 0);
        ApplyRawHeaderOverride(sector, rawHeaderOverride);
        // Mode 0 has no user payload or protection fields; bytes 16..2351 remain zero.
    }

    internal static void BuildMode2Form1Sector(
        long lba,
        ReadOnlySpan<byte> userData,
        byte fileNumber,
        byte channelNumber,
        byte submode,
        byte codingInfo,
        Span<byte> sector,
        bool dicLoggedMode2Form1EccError = false,
        byte[]? rawHeaderOverride = null,
        byte[]? xaSubheaderOverride = null)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));
        if (userData.Length != CookedSectorSize)
            throw new ArgumentException("Mode 2 Form 1 user data must be exactly 2048 bytes.", nameof(userData));

        CdRawSectorCodec.InitializeSector(sector, lba, mode: 2);
        ApplyRawHeaderOverride(sector, rawHeaderOverride);
        submode = (byte)(submode & ~XaForm2Bit);
        if (xaSubheaderOverride is { Length: 8 })
            xaSubheaderOverride.AsSpan().CopyTo(sector.Slice(16, 8));
        else
            WriteXaSubheader(sector, fileNumber, channelNumber, submode, codingInfo);
        userData.CopyTo(sector.Slice(24, CookedSectorSize));
        RebuildErrorFields(
            sector,
            RawSectorPayloadKind.Mode2Form1,
            mode2Form2NoEdc: false,
            dicLoggedMode2Form1EccError: dicLoggedMode2Form1EccError);
    }

    internal static void BuildMode2Form2Sector(
        long lba,
        ReadOnlySpan<byte> userData,
        byte fileNumber,
        byte channelNumber,
        byte submode,
        byte codingInfo,
        Span<byte> sector,
        bool generateEdc = true,
        byte[]? rawHeaderOverride = null,
        byte[]? xaSubheaderOverride = null)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));
        if (userData.Length != 2324)
            throw new ArgumentException("Mode 2 Form 2 user data must be exactly 2324 bytes.", nameof(userData));

        CdRawSectorCodec.InitializeSector(sector, lba, mode: 2);
        ApplyRawHeaderOverride(sector, rawHeaderOverride);
        submode = (byte)(submode | XaForm2Bit);
        if (xaSubheaderOverride is { Length: 8 })
            xaSubheaderOverride.AsSpan().CopyTo(sector.Slice(16, 8));
        else
            WriteXaSubheader(sector, fileNumber, channelNumber, submode, codingInfo);
        userData.CopyTo(sector.Slice(24, 2324));
        RebuildErrorFields(sector, RawSectorPayloadKind.Mode2Form2, mode2Form2NoEdc: !generateEdc);
    }

    /// <summary>
    /// Replaces only the logical user payload of an existing raw data sector and
    /// rebuilds its protection fields. Sync, exact MSF/mode header bytes and both XA
    /// subheader copies are deliberately preserved. This is essential for DIC logs
    /// containing malformed headers or unequal XA subheaders.
    /// </summary>
    internal static void ReplacePayloadPreservingFraming(
        Span<byte> sector,
        ReadOnlySpan<byte> payload,
        bool mode2Form2NoEdc = false,
        bool dicLoggedMode2Form1EccError = false)
    {
        if (sector.Length < RawSectorSize)
            throw new ArgumentException("Sector buffer must be at least 2352 bytes.", nameof(sector));

        RawSectorPayloadKind kind = GetRawPayloadKind(sector.Slice(0, RawSectorSize));
        switch (kind)
        {
            case RawSectorPayloadKind.Mode1:
                if (payload.Length != CookedSectorSize)
                    throw new ArgumentException("Mode 1 payload must be exactly 2048 bytes.", nameof(payload));
                payload.CopyTo(sector.Slice(16, CookedSectorSize));
                break;
            case RawSectorPayloadKind.Mode2Form1:
                if (payload.Length != CookedSectorSize)
                    throw new ArgumentException("Mode 2 Form 1 payload must be exactly 2048 bytes.", nameof(payload));
                payload.CopyTo(sector.Slice(24, CookedSectorSize));
                break;
            case RawSectorPayloadKind.Mode2Form2:
                if (payload.Length != 2324)
                    throw new ArgumentException("Mode 2 Form 2 payload must be exactly 2324 bytes.", nameof(payload));
                payload.CopyTo(sector.Slice(24, 2324));
                break;
            default:
                throw new NotSupportedException("Unsupported raw CD sector mode for payload replacement.");
        }

        RebuildErrorFields(sector, kind, mode2Form2NoEdc, dicLoggedMode2Form1EccError);
    }

    private static void ApplyRawHeaderOverride(Span<byte> sector, byte[]? rawHeaderOverride)
    {
        if (rawHeaderOverride is null)
            return;
        if (rawHeaderOverride.Length is not (3 or 4))
            throw new ArgumentException("Raw header override must contain MSF[3], optionally followed by the exact raw mode byte.", nameof(rawHeaderOverride));
        rawHeaderOverride.AsSpan().CopyTo(sector.Slice(12, rawHeaderOverride.Length));
    }

    private static void WriteXaSubheader(Span<byte> sector, byte fileNumber, byte channelNumber, byte submode, byte codingInfo)
    {
        sector[16] = fileNumber;
        sector[17] = channelNumber;
        sector[18] = submode;
        sector[19] = codingInfo;
        sector[20] = fileNumber;
        sector[21] = channelNumber;
        sector[22] = submode;
        sector[23] = codingInfo;
    }

}
