namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{
    private sealed record JolietPaddingPatch(
        DicJolietPaddingEvidence Evidence,
        long TargetLba,
        int TargetLogicalOffset);

    public async Task<DicJolietPaddingCandidateResult> CreateJolietPaddingCandidateAsync(
        SkeletonInspectionResult inspection,
        string rebuiltImagePath,
        string candidatePath,
        IReadOnlyList<DicJolietPaddingEvidence> donorEvidence,
        IProgress<DicDonorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(donorEvidence);

        string sourcePath = Path.GetFullPath(rebuiltImagePath);
        string destinationPath = Path.GetFullPath(candidatePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The rebuilt image was not found.", sourcePath);
        if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Joliet-padding candidate must use a separate path.", nameof(candidatePath));

        var warnings = new List<string>();
        var patches = new List<JolietPaddingPatch>();
        int alreadyMatching = 0;
        int skipped = 0;
        int targetSectorSize;

        progress?.Report(new DicDonorProgress(0, 1, "Inspecting rebuilt Joliet directory records"));
        await using (DonorImageReader target = await DonorImageReader.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false))
        {
            targetSectorSize = target.SectorSize;
            DonorFilesystem targetFilesystem = await ParseFilesystemAsync(target, cancellationToken).ConfigureAwait(false);
            if (!targetFilesystem.HasJoliet)
                throw new InvalidOperationException("The rebuilt image does not contain a readable Joliet filesystem.");

            Dictionary<string, DicDonorFile[]> targetByPath = targetFilesystem.JolietFiles
                .GroupBy(file => NormalizePath(file.Path), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            foreach (DicJolietPaddingEvidence evidence in donorEvidence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = NormalizePath(evidence.Path);
                if (!targetByPath.TryGetValue(path, out DicDonorFile[]? pathMatches))
                {
                    skipped++;
                    warnings.Add($"Skipped donor padding for '{evidence.Path}': the rebuilt Joliet path was not found with identical casing.");
                    continue;
                }

                DicDonorFile[] exactMatches = pathMatches
                    .Where(file => file.ExtentLba == evidence.ExtentLba)
                    .Where(file => file.DataLength == evidence.DataLength)
                    .Where(file => file.FileFlags == evidence.FileFlags)
                    .Where(file => file.IdentifierLength == evidence.IdentifierLength)
                    .Where(file => file.DirectoryRecordLength == evidence.DirectoryRecordLength)
                    .ToArray();
                if (exactMatches.Length != 1)
                {
                    skipped++;
                    warnings.Add($"Skipped donor padding for '{evidence.Path}': expected one identical Joliet record geometry, found {exactMatches.Length:N0}.");
                    continue;
                }

                DicDonorFile targetRecord = exactMatches[0];
                if (targetRecord.IdentifierPaddingValue is null)
                {
                    skipped++;
                    warnings.Add($"Skipped donor padding for '{evidence.Path}': the rebuilt record has no valid identifier-padding byte.");
                    continue;
                }

                long targetLba = checked(targetRecord.DirectoryExtentLba + targetRecord.DirectoryRecordOffset / CookedSectorSize);
                int logicalOffset = targetRecord.DirectoryRecordOffset % CookedSectorSize + 33 + targetRecord.IdentifierLength;
                if (logicalOffset < 0 || logicalOffset >= CookedSectorSize)
                {
                    skipped++;
                    warnings.Add($"Skipped donor padding for '{evidence.Path}': its padding byte is outside one logical directory sector.");
                    continue;
                }
                if (inspection.DicExactRawSectorOverrides?.ContainsKey(targetLba) == true)
                {
                    skipped++;
                    warnings.Add($"Skipped donor padding for '{evidence.Path}': LBA {targetLba:N0} already has authoritative exact raw-sector evidence.");
                    continue;
                }
                if (targetRecord.IdentifierPaddingValue.Value == evidence.PaddingValue)
                {
                    alreadyMatching++;
                    continue;
                }

                patches.Add(new JolietPaddingPatch(evidence, targetLba, logicalOffset));
            }
        }

        if (patches.Count == 0)
        {
            return new DicJolietPaddingCandidateResult(
                destinationPath,
                0,
                alreadyMatching,
                skipped,
                Array.Empty<DicJolietPaddingEvidence>(),
                warnings);
        }

        try
        {
            await CopyCandidateAsync(sourcePath, destinationPath, progress, cancellationToken).ConfigureAwait(false);
            await ApplyJolietPaddingPatchesAsync(
                inspection,
                destinationPath,
                targetSectorSize,
                patches,
                progress,
                cancellationToken).ConfigureAwait(false);

            return new DicJolietPaddingCandidateResult(
                destinationPath,
                patches.Count,
                alreadyMatching,
                skipped,
                patches.Select(patch => patch.Evidence).ToArray(),
                warnings);
        }
        catch
        {
            TryDeleteCandidate(destinationPath);
            throw;
        }
    }

    private static async Task CopyCandidateAsync(
        string sourcePath,
        string destinationPath,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 4 * 1024 * 1024;
        long total = new FileInfo(sourcePath).Length;
        long copied = 0;
        byte[] buffer = new byte[bufferSize];

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(new DicDonorProgress(copied, total, "Creating temporary donor-padding candidate"));
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyJolietPaddingPatchesAsync(
        SkeletonInspectionResult inspection,
        string candidatePath,
        int sectorSize,
        IReadOnlyList<JolietPaddingPatch> patches,
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

        IGrouping<long, JolietPaddingPatch>[] sectors = patches
            .GroupBy(patch => patch.TargetLba)
            .OrderBy(group => group.Key)
            .ToArray();
        int completed = 0;
        foreach (IGrouping<long, JolietPaddingPatch> sectorPatches in sectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long lba = sectorPatches.Key;
            byte[] sector = new byte[sectorSize];
            stream.Position = checked(lba * sectorSize);
            await ReadExactlyAsync(stream, sector, cancellationToken).ConfigureAwait(false);

            int payloadOffset;
            if (sectorSize == CookedSectorSize)
            {
                payloadOffset = 0;
            }
            else
            {
                byte logicalMode = (byte)(sector[15] & 0x03);
                payloadOffset = logicalMode == 1
                    ? 16
                    : logicalMode == 2 && (sector[18] & 0x20) == 0
                        ? 24
                        : throw new InvalidOperationException($"Joliet directory LBA {lba:N0} is not a Form-1 data sector.");
            }

            foreach (JolietPaddingPatch patch in sectorPatches)
                sector[payloadOffset + patch.TargetLogicalOffset] = patch.Evidence.PaddingValue;

            if (sectorSize == RawSectorSize)
            {
                bool preserveDicQFault = inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true;
                SkeletonResurrectionService.RebuildForm1ProtectionFields(sector, preserveDicQFault);
            }

            stream.Position = checked(lba * sectorSize);
            await stream.WriteAsync(sector.AsMemory(), cancellationToken).ConfigureAwait(false);
            completed++;
            progress?.Report(new DicDonorProgress(completed, sectors.Length, "Applying donor Joliet padding to candidate"));
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteCandidate(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A failed candidate cleanup must not hide the original failure.
        }
    }
}

