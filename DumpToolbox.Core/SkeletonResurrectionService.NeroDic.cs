using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    internal static bool TryApplyRedumperNeroSystemArea(
        SkeletonInspectionResult inspection,
        string imagePath,
        uint targetCrc32,
        IProgress<string>? activity = null,
        IProgress<NeroSystemAreaRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        NeroSystemAreaRecoveryInfo? info = inspection.NeroSystemAreaRecovery;
        if (inspection.SourceKind != SkeletonSourceKind.Redumper || info is null)
            return false;

        int regionLength = inspection.ImageKind == SkeletonImageKind.Cooked2048
            ? NeroSystemAreaBytes
            : SystemAreaSectors * RawSectorSize;
        long imageLength = new FileInfo(imagePath).Length;
        if (imageLength < regionLength)
            return false;

        byte[] originalRegion = ReadFilePrefix(imagePath, regionLength, cancellationToken);
        bool keepGeneratedRegion = false;
        bool regionWasWritten = false;
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new NeroSystemAreaRecoveryProgress(
            0,
            32,
            TimeSpan.Zero,
            UsesWholeImageCrc32: true));

        try
        {
            activity?.Report(
                "NERO SYSTEM_AREA: solving the four private bytes from the Redumper whole-image CRC32 " +
                "using 32 raw-sector influence vectors.");

            byte[] baselineRegion = BuildNeroPhysicalSystemArea(
                inspection,
                info,
                privateValue: 0,
                originalRegion);
            WriteFilePrefix(imagePath, baselineRegion, cancellationToken);
            regionWasWritten = true;

            uint baselineImageCrc = ComputeFileCrc32(imagePath, cancellationToken);
            uint baselineRegionCrc = Crc32.Compute(baselineRegion);
            Crc32.ShiftOperator suffixShift = Crc32.CreateShiftOperator(imageLength - regionLength);
            var effects = new uint[32];

            for (int bit = 0; bit < 32; bit++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] candidateRegion = BuildNeroPhysicalSystemArea(
                    inspection,
                    info,
                    1u << bit,
                    originalRegion);
                uint regionDifference = Crc32.Compute(candidateRegion) ^ baselineRegionCrc;
                effects[bit] = suffixShift.Apply(regionDifference);
                progress?.Report(new NeroSystemAreaRecoveryProgress(
                    bit + 1,
                    32,
                    stopwatch.Elapsed,
                    UsesWholeImageCrc32: true));
            }

            if (!TrySolveCrc32Patch(effects, targetCrc32 ^ baselineImageCrc, out uint privateValue))
            {
                activity?.Report(
                    "NERO SYSTEM_AREA: the Redumper whole-image CRC32 did not yield a supported four-byte value.");
                return false;
            }

            byte[] logicalSystemArea = BuildNeroSystemAreaForPrivateValue(info, privateValue);
            string actualSystemAreaSha1 = Convert.ToHexString(SHA1.HashData(logicalSystemArea)).ToLowerInvariant();
            if (!actualSystemAreaSha1.Equals(info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            {
                activity?.Report(
                    $"NERO SYSTEM_AREA: CRC32 produced candidate private bytes {privateValue:X8}, but its 32 KiB " +
                    $"SYSTEM_AREA SHA-1 {actualSystemAreaSha1} did not match the manifest. The candidate was rejected.");
                return false;
            }

            byte[] finalRegion = BuildNeroPhysicalSystemArea(
                inspection,
                info,
                privateValue,
                originalRegion);
            WriteFilePrefix(imagePath, finalRegion, cancellationToken);
            regionWasWritten = true;
            keepGeneratedRegion = true;
            progress?.Report(new NeroSystemAreaRecoveryProgress(
                32,
                32,
                stopwatch.Elapsed,
                privateValue,
                UsesWholeImageCrc32: true));
            activity?.Report(
                $"NERO SYSTEM_AREA: CRC32 recovered private bytes {privateValue:X8}; " +
                $"generated 32 KiB payload SHA-1 {actualSystemAreaSha1} MATCH.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.Report(
                $"NERO SYSTEM_AREA: Redumper CRC32 reconstruction was not applied: {ex.Message}");
            return false;
        }
        finally
        {
            if (regionWasWritten && !keepGeneratedRegion)
                WriteFilePrefix(imagePath, originalRegion, CancellationToken.None);
        }
    }

    internal static void ApplyNeroSystemAreaForPrivateValue(
        SkeletonInspectionResult inspection,
        string imagePath,
        uint privateValue,
        CancellationToken cancellationToken = default)
    {
        NeroSystemAreaRecoveryInfo info = inspection.NeroSystemAreaRecovery
            ?? throw new InvalidOperationException("The image has no Nero system-area recovery information.");
        int regionLength = inspection.ImageKind == SkeletonImageKind.Cooked2048
            ? NeroSystemAreaBytes
            : SystemAreaSectors * RawSectorSize;
        byte[] originalRegion = ReadFilePrefix(imagePath, regionLength, cancellationToken);
        byte[] generatedRegion = BuildNeroPhysicalSystemArea(
            inspection,
            info,
            privateValue,
            originalRegion);
        WriteFilePrefix(imagePath, generatedRegion, cancellationToken);
    }

    internal static bool TryApplyDicNeroSystemArea(
        SkeletonInspectionResult inspection,
        string imagePath,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        NeroSystemAreaRecoveryInfo? info = inspection.NeroSystemAreaRecovery;
        if (inspection.SourceKind != SkeletonSourceKind.DiscImageCreator ||
            info is not { DetectedFromDic: true, DicSystemAreaLayoutCompatible: true })
        {
            return false;
        }

        bool hasKnownPrivateValue = info.DicKnownPrivateValue is not null;
        bool hasCrcTarget = uint.TryParse(
            inspection.ExpectedImageCrc32,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out uint targetCrc32);
        bool hasCryptographicTarget =
            !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) ||
            !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1);

        if (!hasKnownPrivateValue && (!hasCrcTarget || !hasCryptographicTarget))
            return false;

        int regionLength = inspection.ImageKind == SkeletonImageKind.Cooked2048
            ? NeroSystemAreaBytes
            : SystemAreaSectors * RawSectorSize;
        long imageLength = new FileInfo(imagePath).Length;
        if (imageLength < regionLength)
            return false;

        byte[] originalRegion = ReadFilePrefix(imagePath, regionLength, cancellationToken);
        bool keepGeneratedRegion = false;
        bool regionWasWritten = false;

        try
        {
            uint privateValue;
            if (info.DicKnownPrivateValue is uint preservedValue)
            {
                privateValue = preservedValue;
                activity?.Report(
                    $"NERO SYSTEM AREA: using private bytes {privateValue:X8} preserved directly by DIC offset evidence.");
            }
            else
            {
                activity?.Report(
                    "NERO SYSTEM AREA: solving the four private bytes from the final DIC whole-image CRC32; " +
                    "the result will be retained only if MD5/SHA-1 also verifies.");

                byte[] baselineRegion = BuildNeroPhysicalSystemArea(
                    inspection,
                    info,
                    privateValue: 0,
                    originalRegion);
                WriteFilePrefix(imagePath, baselineRegion, cancellationToken);
                regionWasWritten = true;

                uint baselineImageCrc = ComputeFileCrc32(imagePath, cancellationToken);
                uint baselineRegionCrc = Crc32.Compute(baselineRegion);
                Crc32.ShiftOperator suffixShift = Crc32.CreateShiftOperator(imageLength - regionLength);
                var effects = new uint[32];

                for (int bit = 0; bit < 32; bit++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] candidateRegion = BuildNeroPhysicalSystemArea(
                        inspection,
                        info,
                        1u << bit,
                        originalRegion);
                    uint regionDifference = Crc32.Compute(candidateRegion) ^ baselineRegionCrc;
                    effects[bit] = suffixShift.Apply(regionDifference);
                }

                if (!TrySolveCrc32Patch(effects, targetCrc32 ^ baselineImageCrc, out privateValue))
                {
                    activity?.Report(
                        "NERO SYSTEM AREA: the DIC whole-image CRC32 does not yield a unique supported four-byte Nero value; " +
                        "the original synthetic system area was retained.");
                    return false;
                }
            }

            byte[] finalRegion = BuildNeroPhysicalSystemArea(
                inspection,
                info,
                privateValue,
                originalRegion);
            WriteFilePrefix(imagePath, finalRegion, cancellationToken);
            regionWasWritten = true;

            if (hasKnownPrivateValue && !HasExpectedImageHashes(inspection))
            {
                keepGeneratedRegion = true;
                activity?.Report(
                    $"NERO SYSTEM AREA: generated from DIC-preserved private bytes {privateValue:X8}.");
                return true;
            }

            HashCalculationResult actual = new HashCalculationService()
                .CalculateAsync(
                    imagePath,
                    new HashCalculationOptions(Crc32: true, Md5: true, Sha1: true),
                    cancellationToken: cancellationToken)
                .GetAwaiter()
                .GetResult();

            string crc = actual.Hashes["CRC32"];
            string md5 = actual.Hashes["MD5"];
            string sha1 = actual.Hashes["SHA-1"];
            bool verified = ExpectedInspectionHashesMatch(inspection, crc, md5, sha1);

            // Directly captured private bytes are exact evidence even during a partial
            // cumulative DIC rebuild whose whole-image hashes cannot match yet.
            if (!verified && hasKnownPrivateValue)
            {
                keepGeneratedRegion = true;
                activity?.Report(
                    $"NERO SYSTEM AREA: generated from DIC-preserved private bytes {privateValue:X8}; " +
                    "whole-image verification remains pending until the other missing regions are restored.");
                return true;
            }

            if (!verified)
            {
                activity?.Report(
                    $"NERO SYSTEM AREA: CRC32 produced candidate private bytes {privateValue:X8}, but the DIC MD5/SHA-1 did not match. " +
                    "The candidate was rejected and the previous system area was restored.");
                return false;
            }

            keepGeneratedRegion = true;
            activity?.Report(
                $"NERO SYSTEM AREA: recovered private bytes {privateValue:X8}; whole-image CRC32/MD5/SHA-1 MATCH.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.Report(
                $"NERO SYSTEM AREA: automatic DIC reconstruction was not applied: {ex.Message}");
            return false;
        }
        finally
        {
            if (regionWasWritten && !keepGeneratedRegion)
                WriteFilePrefix(imagePath, originalRegion, CancellationToken.None);
        }
    }

    private static byte[] BuildNeroPhysicalSystemArea(
        SkeletonInspectionResult inspection,
        NeroSystemAreaRecoveryInfo info,
        uint privateValue,
        ReadOnlySpan<byte> originalPhysicalRegion)
    {
        byte[] logicalSystemArea = BuildNeroSystemAreaForPrivateValue(info, privateValue);
        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            return logicalSystemArea;

        if (originalPhysicalRegion.Length != SystemAreaSectors * RawSectorSize)
            throw new InvalidDataException("The DIC raw system-area region is incomplete.");

        byte[] rawSystemArea = originalPhysicalRegion.ToArray();
        for (int lba = 0; lba < SystemAreaSectors; lba++)
        {
            Span<byte> sector = rawSystemArea.AsSpan(lba * RawSectorSize, RawSectorSize);
            ReadOnlySpan<byte> payload = logicalSystemArea.AsSpan(lba * CookedSectorSize, CookedSectorSize);
            bool preserveDicQFault = inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true;
            ReplacePayloadPreservingFraming(
                sector,
                payload,
                mode2Form2NoEdc: false,
                dicLoggedMode2Form1EccError: preserveDicQFault);
        }

        return rawSystemArea;
    }

    private static bool TrySolveCrc32Patch(
        ReadOnlySpan<uint> bitEffects,
        uint targetDifference,
        out uint value)
    {
        if (bitEffects.Length != 32)
            throw new ArgumentException("Exactly 32 CRC32 bit effects are required.", nameof(bitEffects));

        Span<uint> basis = stackalloc uint[32];
        Span<uint> combinations = stackalloc uint[32];

        for (int bit = 0; bit < 32; bit++)
        {
            uint vector = bitEffects[bit];
            uint combination = 1u << bit;
            while (vector != 0)
            {
                int pivot = BitOperations.Log2(vector);
                if (basis[pivot] == 0)
                {
                    basis[pivot] = vector;
                    combinations[pivot] = combination;
                    break;
                }

                vector ^= basis[pivot];
                combination ^= combinations[pivot];
            }
        }

        uint remaining = targetDifference;
        uint solution = 0;
        while (remaining != 0)
        {
            int pivot = BitOperations.Log2(remaining);
            if (basis[pivot] == 0)
            {
                value = 0;
                return false;
            }

            remaining ^= basis[pivot];
            solution ^= combinations[pivot];
        }

        value = solution;
        return true;
    }

    private static byte[] ReadFilePrefix(
        string path,
        int length,
        CancellationToken cancellationToken)
    {
        byte[] data = new byte[length];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize,
            FileOptions.SequentialScan);
        int read = 0;
        while (read < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(data, read, data.Length - read);
            if (count <= 0)
                throw new EndOfStreamException("The reconstructed image ended inside the ISO system area.");
            read += count;
        }
        return data;
    }

    private static void WriteFilePrefix(
        string path,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static uint ComputeFileCrc32(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            uint crc = 0;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc = Crc32.Compute(buffer.AsSpan(0, read), crc);
            }
            return crc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
