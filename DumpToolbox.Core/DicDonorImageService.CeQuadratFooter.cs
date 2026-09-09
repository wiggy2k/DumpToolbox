using System.Text;

namespace DumpToolbox.Core;

public enum DicCeQuadratFooterLayout
{
    ExactDonorEndOfVolumeSectors,
    TextAtFinalVolumeSector,
    TextAtPenultimateWithBasicBinaryFinal,
    TextAtPenultimateWithExtendedBinaryFinal
}

public sealed record DicCeQuadratFooterAttempt(
    DicCeQuadratFooterLayout Layout,
    string Description,
    HashCalculationResult Hashes,
    bool MatchesTarget);

public sealed record DicCeQuadratFooterRecoveryResult(
    bool Eligible,
    bool Matched,
    DicCeQuadratFooterLayout? MatchedLayout,
    IReadOnlyList<DicCeQuadratFooterAttempt> Attempts,
    IReadOnlyList<string> Warnings);

public sealed partial class DicDonorImageService
{
    public async Task<DicCeQuadratFooterRecoveryResult> TryRecoverCeQuadratFooterAsync(
        SkeletonInspectionResult inspection,
        string rebuiltImagePath,
        string? donorImagePath = null,
        IProgress<DicDonorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        string rebuiltPath = Path.GetFullPath(rebuiltImagePath);
        if (!File.Exists(rebuiltPath))
            throw new FileNotFoundException("The rebuilt image was not found.", rebuiltPath);

        var warnings = new List<string>();
        var attempts = new List<DicCeQuadratFooterAttempt>();
        if (string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) &&
            string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1))
        {
            warnings.Add("CeQuadrat footer variants were not tested because no target MD5 or SHA1 is available to verify a unique byte-exact result.");
            return new DicCeQuadratFooterRecoveryResult(false, false, null, attempts, warnings);
        }

        int sectorSize;
        long penultimateLba;
        long finalLba;
        byte[] rebuiltPvd;
        byte[] originalPenultimate;
        byte[] originalFinal;
        string? penultimateConflict;
        string? finalConflict;
        await using (DonorImageReader image = await DonorImageReader.OpenAsync(rebuiltPath, cancellationToken).ConfigureAwait(false))
        {
            DonorFilesystem filesystem = await ParseFilesystemAsync(image, cancellationToken).ConfigureAwait(false);
            if (filesystem.Pvd is null || !filesystem.HasJoliet || !IsCeQuadratOrWinOnCd(filesystem.Pvd))
                return new DicCeQuadratFooterRecoveryResult(false, false, null, attempts, warnings);
            rebuiltPvd = filesystem.Pvd;

            uint volumeSectors = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(filesystem.Pvd.AsSpan(80, 4));
            if (volumeSectors < 2 || volumeSectors > image.SectorCount)
            {
                warnings.Add("CeQuadrat footer variants were not tested because the PVD volume size does not identify two valid end-of-volume sectors.");
                return new DicCeQuadratFooterRecoveryResult(false, false, null, attempts, warnings);
            }

            penultimateLba = volumeSectors - 2L;
            finalLba = volumeSectors - 1L;
            sectorSize = image.SectorSize;
            originalPenultimate = await image.ReadForm1SectorAsync(penultimateLba, cancellationToken).ConfigureAwait(false);
            originalFinal = await image.ReadForm1SectorAsync(finalLba, cancellationToken).ConfigureAwait(false);
            penultimateConflict = GetCeQuadratSectorConflict(inspection, filesystem, penultimateLba, originalPenultimate);
            finalConflict = GetCeQuadratSectorConflict(inspection, filesystem, finalLba, originalFinal);
        }

        string candidatePath = rebuiltPath + $".{Guid.NewGuid():N}.cequadrat-footer-candidate";
        var candidates = new List<(DicCeQuadratFooterLayout Layout, string Description, byte[] Penultimate, byte[] Final)>();
        bool AddCandidate(DicCeQuadratFooterLayout layout, string description, byte[] penultimate, byte[] final)
        {
            if (!penultimate.AsSpan().SequenceEqual(originalPenultimate) && penultimateConflict is not null)
            {
                warnings.Add($"Skipped CeQuadrat candidate '{description}': VSS-2 LBA {penultimateLba:N0} {penultimateConflict}.");
                return false;
            }
            if (!final.AsSpan().SequenceEqual(originalFinal) && finalConflict is not null)
            {
                warnings.Add($"Skipped CeQuadrat candidate '{description}': VSS-1 LBA {finalLba:N0} {finalConflict}.");
                return false;
            }
            if (penultimate.AsSpan().SequenceEqual(originalPenultimate) && final.AsSpan().SequenceEqual(originalFinal))
                return false;
            candidates.Add((layout, description, penultimate, final));
            return true;
        }

        DicCeQuadratFooterLayout? donorObservedLayout = null;
        if (!string.IsNullOrWhiteSpace(donorImagePath) && File.Exists(donorImagePath))
        {
            try
            {
                await using DonorImageReader donor = await DonorImageReader.OpenAsync(donorImagePath, cancellationToken).ConfigureAwait(false);
                DonorFilesystem donorFilesystem = await ParseFilesystemAsync(donor, cancellationToken).ConfigureAwait(false);
                uint donorVolumeSectors = donorFilesystem.Pvd is null
                    ? 0
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(donorFilesystem.Pvd.AsSpan(80, 4));
                bool donorFooterReadable = donorFilesystem.Pvd is not null &&
                                           donorFilesystem.HasJoliet &&
                                           IsCeQuadratOrWinOnCd(donorFilesystem.Pvd) &&
                                           donorVolumeSectors >= 2 &&
                                           donorVolumeSectors <= donor.SectorCount;
                if (donorFooterReadable)
                {
                    long donorPenultimateLba = donorVolumeSectors - 2L;
                    long donorFinalLba = donorVolumeSectors - 1L;
                    byte[] donorPenultimate = await donor.ReadForm1SectorAsync(donorPenultimateLba, cancellationToken).ConfigureAwait(false);
                    byte[] donorFinal = await donor.ReadForm1SectorAsync(donorFinalLba, cancellationToken).ConfigureAwait(false);
                    bool sameFilesystemIdentity = donorVolumeSectors == finalLba + 1 &&
                                                  donorFilesystem.Pvd!.AsSpan().SequenceEqual(rebuiltPvd);
                    bool exactDonorAdded = false;
                    if (sameFilesystemIdentity &&
                        (donorPenultimate.Any(value => value != 0) || donorFinal.Any(value => value != 0)))
                    {
                        exactDonorAdded = AddCandidate(
                            DicCeQuadratFooterLayout.ExactDonorEndOfVolumeSectors,
                            "exact end-of-volume payload sectors from donor",
                            donorPenultimate,
                            donorFinal);
                    }

                    donorObservedLayout = IdentifyCeQuadratFooterLayout(
                        donorPenultimate,
                        donorFinal,
                        donorPenultimateLba,
                        donorFinalLba);
                    if (donorObservedLayout is not null && !exactDonorAdded)
                    {
                        (byte[] targetPenultimate, byte[] targetFinal) = BuildCeQuadratFooterLayout(
                            donorObservedLayout.Value,
                            penultimateLba,
                            finalLba);
                        if (donorObservedLayout == DicCeQuadratFooterLayout.TextAtFinalVolumeSector)
                            targetPenultimate = originalPenultimate;
                        AddCandidate(
                            donorObservedLayout.Value,
                            $"donor-observed {DescribeCeQuadratFooterLayout(donorObservedLayout.Value)}, regenerated for target LBAs",
                            targetPenultimate,
                            targetFinal);
                    }
                }
                else
                {
                    warnings.Add("The selected donor was checked for CeQuadrat footer sectors, but it does not contain a readable CeQuadrat/WinOnCD Joliet volume footer location.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"The selected donor could not be checked for CeQuadrat footer sectors: {ex.Message}");
            }
        }

        DicCeQuadratFooterLayout[] deterministicLayouts =
        {
            DicCeQuadratFooterLayout.TextAtFinalVolumeSector,
            DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal,
            DicCeQuadratFooterLayout.TextAtPenultimateWithExtendedBinaryFinal
        };
        foreach (DicCeQuadratFooterLayout layout in deterministicLayouts)
        {
            if (layout == donorObservedLayout)
                continue;
            (byte[] penultimate, byte[] final) = BuildCeQuadratFooterLayout(layout, penultimateLba, finalLba);
            if (layout == DicCeQuadratFooterLayout.TextAtFinalVolumeSector)
                penultimate = originalPenultimate;
            AddCandidate(layout, DescribeCeQuadratFooterLayout(layout), penultimate, final);
        }
        if (candidates.Count == 0)
            return new DicCeQuadratFooterRecoveryResult(false, false, null, attempts, warnings);
        var options = new HashCalculationOptions(
            Crc32: !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32),
            Md5: !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5),
            Sha1: !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1));

        try
        {
            await CopyCandidateAsync(
                rebuiltPath,
                candidatePath,
                progress,
                cancellationToken,
                "Creating temporary CeQuadrat footer candidate").ConfigureAwait(false);

            byte[] candidatePenultimate = originalPenultimate;
            byte[] candidateFinal = originalFinal;
            foreach ((DicCeQuadratFooterLayout layout, string description, byte[] penultimate, byte[] final) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyCeQuadratFooterPayloadsAsync(
                    inspection,
                    candidatePath,
                    sectorSize,
                    penultimateLba,
                    penultimate.AsSpan().SequenceEqual(candidatePenultimate) ? null : penultimate,
                    finalLba,
                    final.AsSpan().SequenceEqual(candidateFinal) ? null : final,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                candidatePenultimate = penultimate;
                candidateFinal = final;

                var hashProgress = new Progress<HashCalculationProgress>(hash =>
                    progress?.Report(new DicDonorProgress(hash.BytesRead, hash.TotalBytes, $"Hashing CeQuadrat candidate: {description}")));
                HashCalculationResult hashes = await new HashCalculationService().CalculateAsync(
                    candidatePath,
                    options,
                    hashProgress,
                    cancellationToken).ConfigureAwait(false);
                bool matches = MatchesExpectedHashes(inspection, hashes);
                attempts.Add(new DicCeQuadratFooterAttempt(layout, description, hashes, matches));
                if (!matches)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(candidatePath, rebuiltPath, true);
                return new DicCeQuadratFooterRecoveryResult(true, true, layout, attempts, warnings);
            }

            return new DicCeQuadratFooterRecoveryResult(true, false, null, attempts, warnings);
        }
        finally
        {
            TryDeleteCandidate(candidatePath);
        }
    }

    internal static (byte[] Penultimate, byte[] Final) BuildCeQuadratFooterLayout(
        DicCeQuadratFooterLayout layout,
        long penultimateLba,
        long finalLba)
        => layout switch
        {
            DicCeQuadratFooterLayout.ExactDonorEndOfVolumeSectors =>
                throw new ArgumentException("Exact donor sectors must be supplied by the donor image.", nameof(layout)),
            DicCeQuadratFooterLayout.TextAtFinalVolumeSector =>
                (new byte[CeQuadratFooterCodec.PayloadSize], CeQuadratFooterCodec.BuildTextPayload(finalLba)),
            DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal =>
                (CeQuadratFooterCodec.BuildTextPayload(penultimateLba),
                    CeQuadratFooterCodec.BuildBinaryPayload(finalLba, CeQuadratBinaryFooterVariant.Basic)),
            DicCeQuadratFooterLayout.TextAtPenultimateWithExtendedBinaryFinal =>
                (CeQuadratFooterCodec.BuildTextPayload(penultimateLba),
                    CeQuadratFooterCodec.BuildBinaryPayload(finalLba, CeQuadratBinaryFooterVariant.Extended)),
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };

    private static string DescribeCeQuadratFooterLayout(DicCeQuadratFooterLayout layout)
        => layout switch
        {
            DicCeQuadratFooterLayout.ExactDonorEndOfVolumeSectors => "exact end-of-volume payload sectors from donor",
            DicCeQuadratFooterLayout.TextAtFinalVolumeSector => "text block at VSS-1",
            DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal => "text at VSS-2 plus basic binary at VSS-1",
            DicCeQuadratFooterLayout.TextAtPenultimateWithExtendedBinaryFinal => "text at VSS-2 plus extended binary at VSS-1",
            _ => layout.ToString()
        };

    internal static DicCeQuadratFooterLayout? IdentifyCeQuadratFooterLayout(
        ReadOnlySpan<byte> penultimate,
        ReadOnlySpan<byte> final,
        long penultimateLba,
        long finalLba)
    {
        if (CeQuadratFooterCodec.IsExactTextPayload(final, finalLba))
            return DicCeQuadratFooterLayout.TextAtFinalVolumeSector;

        if (!CeQuadratFooterCodec.IsExactTextPayload(penultimate, penultimateLba) ||
            !CeQuadratFooterCodec.TryClassifyExactBinaryPayload(final, finalLba, out CeQuadratBinaryFooterVariant variant))
            return null;
        return variant == CeQuadratBinaryFooterVariant.Basic
            ? DicCeQuadratFooterLayout.TextAtPenultimateWithBasicBinaryFinal
            : DicCeQuadratFooterLayout.TextAtPenultimateWithExtendedBinaryFinal;
    }

    private static bool IsCeQuadratOrWinOnCd(byte[] pvd)
    {
        const int dataPreparerOffset = 446;
        const int dataPreparerLength = 128;
        string preparer = Encoding.ASCII.GetString(pvd, dataPreparerOffset, dataPreparerLength).TrimEnd(' ', '\0');
        return preparer.Contains("CEQUADRAT", StringComparison.OrdinalIgnoreCase) ||
               preparer.Contains("CEQUDRAT", StringComparison.OrdinalIgnoreCase) ||
               preparer.Contains("WINONCD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOccupiedByFilesystemEntry(IEnumerable<DicDonorFile> files, long lba)
    {
        foreach (DicDonorFile file in files)
        {
            IEnumerable<DicDonorExtent> extents = file.Extents ??
                new[] { new DicDonorExtent(file.ExtentLba, file.DataLength, file.ExtendedAttributeRecordLength) };
            foreach (DicDonorExtent extent in extents)
            {
                long sectors = extent.ExtendedAttributeRecordLength +
                               (extent.DataLength <= 0 ? 0 : (extent.DataLength + CookedSectorSize - 1) / CookedSectorSize);
                if (sectors > 0 && lba >= extent.ExtentLba && lba < extent.ExtentLba + sectors)
                    return true;
            }
        }
        return false;
    }

    private static string? GetCeQuadratSectorConflict(
        SkeletonInspectionResult inspection,
        DonorFilesystem filesystem,
        long lba,
        byte[] currentPayload)
    {
        if (inspection.DicExactRawSectorOverrides?.ContainsKey(lba) == true)
            return "has an authoritative exact raw-sector override";
        if (inspection.DicExactZeroSectorLbas?.Contains(lba) == true)
            return "is explicitly proven to be an all-zero raw sector";
        if (inspection.DicExactMainInfoLbas?.Contains(lba) == true)
            return "is preserved by exact mainInfo evidence";
        if (filesystem.MetadataLbas.Contains(lba))
            return "is occupied by primary filesystem metadata";
        if (IsOccupiedByFilesystemEntry(filesystem.Files, lba) ||
            IsOccupiedByFilesystemEntry(filesystem.JolietFiles, lba))
            return "is occupied by a filesystem entry";
        if (currentPayload.Any(value => value != 0))
            return "already contains non-zero payload bytes";
        return null;
    }

    private static async Task ApplyCeQuadratFooterPayloadsAsync(
        SkeletonInspectionResult inspection,
        string candidatePath,
        int sectorSize,
        long penultimateLba,
        byte[]? penultimatePayload,
        long finalLba,
        byte[]? finalPayload,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            candidatePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        (long Lba, byte[] Payload)[] patches = new[]
            {
                (Lba: penultimateLba, Payload: penultimatePayload),
                (Lba: finalLba, Payload: finalPayload)
            }
            .Where(patch => patch.Payload is not null)
            .Select(patch => (patch.Lba, patch.Payload!))
            .ToArray();
        for (int index = 0; index < patches.Length; index++)
        {
            (long lba, byte[] payload) = patches[index];
            byte[] sector = new byte[sectorSize];
            stream.Position = checked(lba * sectorSize);
            await ReadExactlyAsync(stream, sector, cancellationToken).ConfigureAwait(false);
            int payloadOffset = sectorSize == CookedSectorSize
                ? 0
                : (sector[15] & 0x03) == 1
                    ? 16
                    : (sector[15] & 0x03) == 2 && (sector[18] & 0x20) == 0
                        ? 24
                        : throw new InvalidOperationException($"CeQuadrat footer LBA {lba:N0} is not a Form-1 data sector.");
            payload.CopyTo(sector, payloadOffset);
            if (sectorSize == RawSectorSize)
            {
                bool preserveDicQFault = inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true;
                SkeletonResurrectionService.RebuildForm1ProtectionFields(sector, preserveDicQFault);
            }
            stream.Position = checked(lba * sectorSize);
            await stream.WriteAsync(sector.AsMemory(), cancellationToken).ConfigureAwait(false);
            progress?.Report(new DicDonorProgress(index + 1, patches.Length, "Applying deterministic CeQuadrat footer layout"));
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesExpectedHashes(SkeletonInspectionResult inspection, HashCalculationResult hashes)
    {
        static bool Match(IReadOnlyDictionary<string, string> actual, string algorithm, string? expected)
            => string.IsNullOrWhiteSpace(expected) ||
               actual.TryGetValue(algorithm, out string? value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);

        return Match(hashes.Hashes, "CRC32", inspection.ExpectedImageCrc32) &&
               Match(hashes.Hashes, "MD5", inspection.ExpectedImageMd5) &&
               Match(hashes.Hashes, "SHA-1", inspection.ExpectedImageSha1);
    }
}
