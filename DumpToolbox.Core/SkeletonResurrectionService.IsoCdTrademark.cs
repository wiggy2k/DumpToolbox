using System.Reflection;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private sealed record IsoCdTrademarkDescriptor(long Lba, int Length, string ResourceName, string DisplayName);

    /// <summary>
    /// ISOCD 1.04 by Pantaray stores a non-filesystem CDTV/CD32 trademark payload in
    /// sectors identified by an FS/TM record in the PVD Application Use field. DIC
    /// volDesc records the FS/TM bytes but ordinary extracted source files cannot carry
    /// this data, so a synthetic skeleton otherwise leaves the region zero-filled.
    ///
    /// Restore only when the target bytes are still all zero. Existing non-zero bytes
    /// are stronger evidence (for example an exact donor or a skeleton which already
    /// captured the trademark data) and are never overwritten.
    /// </summary>
    private static void ApplyIsoCdTrademarkPayload(
        SkeletonInspectionResult inspection,
        string imagePath,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadIsoCdTrademarkDescriptor(inspection, imagePath, out IsoCdTrademarkDescriptor? descriptor, out string? reason) ||
            descriptor is null)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                activity?.Report($"ISOCD TM: {reason}");
            return;
        }

        byte[] payload = LoadEmbeddedTrademark(descriptor.ResourceName);
        if (payload.Length != descriptor.Length)
        {
            activity?.Report($"ISOCD TM: embedded {descriptor.DisplayName} is {payload.Length:N0} bytes but the PVD requests {descriptor.Length:N0}; leaving the target unchanged.");
            return;
        }

        long sectorCount = (descriptor.Length + CookedSectorSize - 1L) / CookedSectorSize;
        if (descriptor.Lba < 0 || descriptor.Lba + sectorCount > inspection.SectorCount)
        {
            activity?.Report($"ISOCD TM: PVD requests {descriptor.DisplayName} at LBA {descriptor.Lba:N0} for {descriptor.Length:N0} bytes, outside the reconstructed image; ignored.");
            return;
        }

        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        byte[] sector = new byte[inspection.ImageKind == SkeletonImageKind.Raw2352 ? RawSectorSize : CookedSectorSize];

        // First inspect the exact byte range. No-op when it is already correct; refuse
        // to replace any conflicting non-zero evidence.
        int compared = 0;
        bool allZero = true;
        bool exact = true;
        for (long i = 0; i < sectorCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(CookedSectorSize, descriptor.Length - compared);
            ReadSector(stream, inspection, descriptor.Lba + i, sector);
            ReadOnlySpan<byte> user = GetUserDataSpan(inspection, sector);
            ReadOnlySpan<byte> current = user.Slice(0, chunkLength);
            ReadOnlySpan<byte> wanted = payload.AsSpan(compared, chunkLength);
            if (!current.SequenceEqual(wanted)) exact = false;
            for (int j = 0; j < current.Length; j++)
                if (current[j] != 0) { allZero = false; break; }
            compared += chunkLength;
        }

        if (exact)
        {
            activity?.Report($"ISOCD TM: {descriptor.DisplayName} already present exactly at LBA {descriptor.Lba:N0} ({descriptor.Length:N0} bytes); no change required.");
            return;
        }

        if (!allZero)
        {
            activity?.Report($"ISOCD TM: FS/TM requests {descriptor.DisplayName} at LBA {descriptor.Lba:N0}, but that range contains non-zero data which differs from the embedded standard payload; preserving the existing evidence.");
            return;
        }

        int payloadOffset = 0;
        for (long i = 0; i < sectorCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long lba = descriptor.Lba + i;
            int chunkLength = Math.Min(CookedSectorSize, descriptor.Length - payloadOffset);
            ReadSector(stream, inspection, lba, sector);

            if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            {
                payload.AsSpan(payloadOffset, chunkLength).CopyTo(sector.AsSpan(0, chunkLength));
            }
            else
            {
                Span<byte> user = GetUserDataSpan(inspection, sector);
                payload.AsSpan(payloadOffset, chunkLength).CopyTo(user.Slice(0, chunkLength));
                ReplacePayloadPreservingFraming(
                    sector,
                    user,
                    inspection.NoEdcLbas?.Contains(lba) == true,
                    inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true);
            }

            WriteSector(stream, inspection, lba, sector);
            payloadOffset += chunkLength;
        }

        stream.Flush(true);
        activity?.Report($"ISOCD TM: restored embedded {descriptor.DisplayName} ({descriptor.Length:N0} bytes) at PVD-declared LBA {descriptor.Lba:N0}; regenerated raw CD protection fields where applicable.");
    }

    private static bool TryReadIsoCdTrademarkDescriptor(
        SkeletonInspectionResult inspection,
        string imagePath,
        out IsoCdTrademarkDescriptor? descriptor,
        out string? reason)
    {
        descriptor = null;
        reason = null;
        if (inspection.SectorCount <= 16 || !File.Exists(imagePath))
            return false;

        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.RandomAccess);
        byte[] sector = new byte[inspection.ImageKind == SkeletonImageKind.Raw2352 ? RawSectorSize : CookedSectorSize];

        // ISO9660 normally starts at LBA 16. Scan a small descriptor window so a boot
        // record preceding the PVD does not defeat the detector.
        int max = (int)Math.Min(31, inspection.SectorCount - 1);
        for (long lba = 16; lba <= max; lba++)
        {
            ReadSector(stream, inspection, lba, sector);
            ReadOnlySpan<byte> pvd = GetUserDataSpan(inspection, sector);
            if (pvd.Length < 1395 || pvd[0] != 1 || !pvd.Slice(1, 5).SequenceEqual("CD001"u8))
                continue;

            string preparer = Encoding.ASCII.GetString(pvd.Slice(446, 128)).TrimEnd('\0', ' ');
            if (!preparer.Contains("ISOCD", StringComparison.OrdinalIgnoreCase) ||
                !preparer.Contains("Pantaray", StringComparison.OrdinalIgnoreCase))
                return false;

            ReadOnlySpan<byte> app = pvd.Slice(883, 512);
            // ISOCD FS/TM record observed on both CDTV and CD32 masters:
            // 00 'F' 'S' 00 00 'T' 'M' 00 14 00 [24-bit BE length] 00 [24-bit BE LBA]
            if (app[0] != 0 || app[1] != (byte)'F' || app[2] != (byte)'S' || app[3] != 0 ||
                app[4] != 0 || app[5] != (byte)'T' || app[6] != (byte)'M' || app[7] != 0 || app[8] != 0x14)
            {
                reason = "ISOCD/Pantaray PVD found, but its Application Use field does not contain the supported FS/TM record; no trademark payload was inferred.";
                return false;
            }

            int length = (app[10] << 16) | (app[11] << 8) | app[12];
            long tmLba = (app[14] << 16) | (app[15] << 8) | app[16];
            if (length == 22_152)
            {
                descriptor = new IsoCdTrademarkDescriptor(tmLba, length, "DumpToolbox.Core.CDTV.TM", "CDTV.TM");
                return true;
            }
            if (length == 2_048)
            {
                descriptor = new IsoCdTrademarkDescriptor(tmLba, length, "DumpToolbox.Core.CD32.TM", "CD32.TM");
                return true;
            }

            reason = $"ISOCD/Pantaray FS/TM record requests an unrecognised {length:N0}-byte trademark payload at LBA {tmLba:N0}; only the proven 22,152-byte CDTV.TM and 2,048-byte CD32.TM assets are available.";
            return false;
        }

        return false;
    }

    private static byte[] LoadEmbeddedTrademark(string resourceName)
    {
        Assembly assembly = typeof(SkeletonResurrectionService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded ISOCD trademark resource '{resourceName}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void ReadSector(FileStream stream, SkeletonInspectionResult inspection, long lba, byte[] sector)
    {
        long offset = checked(lba * inspection.SectorSize);
        stream.Position = offset;
        int read = 0;
        while (read < sector.Length)
        {
            int got = stream.Read(sector, read, sector.Length - read);
            if (got <= 0) throw new EndOfStreamException($"Unexpected EOF reading LBA {lba:N0}.");
            read += got;
        }
    }

    private static void WriteSector(FileStream stream, SkeletonInspectionResult inspection, long lba, byte[] sector)
    {
        stream.Position = checked(lba * inspection.SectorSize);
        stream.Write(sector, 0, sector.Length);
    }

    private static Span<byte> GetUserDataSpan(SkeletonInspectionResult inspection, byte[] sector)
    {
        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            return sector.AsSpan(0, CookedSectorSize);

        if (sector.Length < RawSectorSize)
            throw new InvalidDataException("Raw sector is truncated.");
        return sector[15] switch
        {
            1 => sector.AsSpan(16, CookedSectorSize),
            2 => sector.AsSpan(24, CookedSectorSize),
            _ => throw new InvalidDataException($"ISOCD trademark sector has unsupported raw CD mode {sector[15]}.")
        };
    }
}
