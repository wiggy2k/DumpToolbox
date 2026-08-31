using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{
    private static async Task<int> ApplyMetadataAsync(
        string targetRawImagePath,
        DonorImageReader donor,
        IReadOnlyCollection<long> metadataLbas,
        SkeletonInspectionResult inspection,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetRawImagePath))
            throw new FileNotFoundException("DIC synthetic skeleton not found.", targetRawImagePath);

        await using var target = new FileStream(
            targetRawImagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            4 * 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        byte[] raw = inspection.ImageKind == SkeletonImageKind.Raw2352
            ? new byte[RawSectorSize]
            : Array.Empty<byte>();
        int targetSectorSize = inspection.ImageKind == SkeletonImageKind.Raw2352 ? RawSectorSize : CookedSectorSize;
        int applied = 0;
        long completed = 0;
        foreach (long lba in metadataLbas.OrderBy(v => v))
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed++;
            long targetIndex = lba - inspection.BaseLba;
            if (targetIndex < 0 || targetIndex >= target.Length / targetSectorSize)
                continue;

            byte[] payload;
            try
            {
                payload = await donor.ReadForm1SectorAsync(lba, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            {
                target.Position = targetIndex * CookedSectorSize;
                await target.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken).ConfigureAwait(false);
                applied++;
                progress?.Report(new DicDonorProgress(completed, metadataLbas.Count, $"Applying donor metadata — LBA {lba:N0}"));
                continue;
            }

            target.Position = targetIndex * RawSectorSize;
            await ReadExactlyAsync(target, raw, cancellationToken).ConfigureAwait(false);
            byte logicalMode = (byte)(raw[15] & 0x03);
            if (logicalMode == 1)
            {
                SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                    raw,
                    payload,
                    mode2Form2NoEdc: false,
                    dicLoggedMode2Form1EccError: false);
            }
            else if (logicalMode == 2)
            {
                if ((raw[18] & 0x20) != 0)
                    continue; // Form 2 has 2324 bytes and cannot be represented by a normal ISO sector.

                SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                    raw,
                    payload,
                    mode2Form2NoEdc: false,
                    dicLoggedMode2Form1EccError: inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true);
            }
            else
            {
                continue;
            }

            SkeletonResurrectionService.ApplyDicFinalSectorRecipes(
                inspection,
                lba,
                raw.AsSpan(0, raw.Length));

            target.Position = targetIndex * RawSectorSize;
            await target.WriteAsync(raw.AsMemory(0, raw.Length), cancellationToken).ConfigureAwait(false);
            applied++;
            progress?.Report(new DicDonorProgress(completed, metadataLbas.Count, $"Applying donor metadata — LBA {lba:N0}"));
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private static async Task<byte[]?> ReadTargetLogicalSectorAsync(
        SkeletonInspectionResult inspection,
        long lba,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inspection.SkeletonPath))
            return null;

        await using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        long index = lba - inspection.BaseLba;
        if (index < 0)
            return null;

        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
        {
            if (stream.Length < (index + 1) * CookedSectorSize)
                return null;
            byte[] cooked = new byte[CookedSectorSize];
            stream.Position = index * CookedSectorSize;
            await ReadExactlyAsync(stream, cooked, cancellationToken).ConfigureAwait(false);
            return cooked;
        }

        if (stream.Length < (index + 1) * RawSectorSize)
            return null;

        byte[] raw = new byte[RawSectorSize];
        stream.Position = index * RawSectorSize;
        await ReadExactlyAsync(stream, raw, cancellationToken).ConfigureAwait(false);
        byte logicalMode = (byte)(raw[15] & 0x03);
        if (logicalMode == 1)
            return raw.AsSpan(16, CookedSectorSize).ToArray();
        if (logicalMode == 2 && (raw[18] & 0x20) == 0)
            return raw.AsSpan(24, CookedSectorSize).ToArray();
        return null;
    }

}
