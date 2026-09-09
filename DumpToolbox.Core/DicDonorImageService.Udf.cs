namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{
    private async Task<IsoExtractionResult> ExtractUdfAsync(
        string sourcePath,
        string root,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        using UdfImageReader udf = UdfImageReader.Open(sourcePath);
        UdfImageFile[] files = udf.Files.ToArray();
        var visible = new HashSet<UdfImageFile>();
        foreach (IGrouping<string, UdfImageFile> group in files.GroupBy(
                     file => NormalizePath(file.Path),
                     StringComparer.OrdinalIgnoreCase))
        {
            UdfImageFile? winner = group.FirstOrDefault();
            if (winner is not null)
                visible.Add(winner);
        }

        int duplicateRecords = files.Length - visible.Count;
        var manifest = new IsoExtractionManifest
        {
            SourceImageName = Path.GetFileName(sourcePath),
            SourceSectorSize = udf.SourceSectorSize,
            SourceFilesystem = "UDF",
            VolumeIdentifier = udf.VolumeIdentifier,
            PvdSha256 = string.Empty,
            HasJoliet = false,
            HasUdf = true,
            VisibleNamespace = "UDF"
        };

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdfImageFile file = files[i];
            string relativePath = visible.Contains(file)
                ? BuildFilesystemExtractionPath(file.Path)
                : BuildPrivateUdfExtractionPath(file, i);
            string destination = Path.Combine(root, relativePath);
            await ExtractUdfFileAsync(udf, file.Path, destination, cancellationToken).ConfigureAwait(false);

            manifest.Files.Add(new IsoExtractionManifestFile
            {
                // Keep a path alias for DIC payload-only matching. UdfPath is the
                // authoritative source identity; none of the ISO geometry fields are.
                IsoPath = file.Path,
                UdfPath = file.Path,
                ExtractedRelativePath = relativePath.Replace('\\', '/'),
                DataLength = file.Length,
                FileFlags = 0
            });
            progress?.Report(new DicDonorProgress(
                i + 1,
                Math.Max(1, files.Length),
                $"Extracting UDF files — {i + 1:N0}/{files.Length:N0}"));
        }

        var warnings = new List<string>
        {
            "UDF-only image detected. No ISO9660 Primary Volume Descriptor or Joliet Supplementary Volume Descriptor is present; the visible extraction tree comes from UDF."
        };
        if (duplicateRecords > 0)
            warnings.Add($"Preserved {duplicateRecords:N0} host-path collision(s) in the private record area.");

        await IsoExtractionManifestService.SaveAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.Combine(root, IsoExtractionManifestService.ManifestFileName);
        progress?.Report(new DicDonorProgress(files.Length, Math.Max(1, files.Length), "Extraction complete"));

        return new IsoExtractionResult(
            sourcePath,
            root,
            manifestPath,
            udf.SourceSectorSize,
            udf.VolumeIdentifier,
            files.Length,
            0,
            duplicateRecords,
            false,
            0,
            true,
            warnings);
    }

    private async Task<DicDonorScanResult> MatchUdfAsync(
        SkeletonInspectionResult inspection,
        string donorPath,
        string cacheRoot,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        using UdfImageReader udf = UdfImageReader.Open(donorPath);
        DicDonorFile[] files = udf.Files
            .Select(file => new DicDonorFile(
                NormalizePath(file.Path),
                0,
                file.Length))
            .ToArray();

        SkeletonContentEntry[] required = inspection.Entries
            .Where(entry => entry.CanRestore && entry.RequiresSource && !entry.IsEmpty)
            .ToArray();
        var bySize = files
            .GroupBy(file => file.DataLength)
            .ToDictionary(group => group.Key, group => group.ToList());
        var udfNames = files.ToDictionary(file => file, file => file);
        var claims = new Dictionary<DicDonorFile, HashSet<uint>>();
        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>
        {
            "The donor is UDF-only. It is used strictly as a logical file-payload source; ISO9660 metadata, raw sectors, exactness regions and Joliet identity are not inferred from UDF."
        };
        JolietNamingProfile? namingProfile = JolietNamingRuleService.ResolveForInspection(inspection, out _, out _);
        string donorCache = Path.Combine(Path.GetFullPath(cacheRoot), BuildDonorCacheId(donorPath));
        Directory.CreateDirectory(donorCache);

        int completed = 0;
        foreach (SkeletonContentEntry entry in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DonorPayloadSelection? selection = FindStrongDonorPayloadMatch(
                entry,
                required,
                bySize,
                udfNames,
                namingProfile);

            if (selection is not null &&
                claims.TryGetValue(selection.File, out HashSet<uint>? claimedExtents) &&
                !claimedExtents.Contains(entry.ExtentLba))
            {
                selection = null;
            }

            if (selection is not null && entry.ContainsMode2Form2)
            {
                warnings.Add($"'{entry.Path}' includes Mode 2 Form 2 sectors. A UDF logical file stream does not preserve the full raw 2324-byte sector payload, so it was not accepted.");
                selection = null;
            }

            if (selection is not null)
            {
                if (!claims.TryGetValue(selection.File, out HashSet<uint>? selectedClaims))
                    claims[selection.File] = selectedClaims = new HashSet<uint>();
                selectedClaims.Add(entry.ExtentLba);

                string extractedPath = BuildCachedSourcePath(donorCache, entry, selection.File);
                if (!File.Exists(extractedPath) || new FileInfo(extractedPath).Length != entry.DataLength)
                    await ExtractUdfFileAsync(udf, selection.File.Path, extractedPath, cancellationToken).ConfigureAwait(false);

                string method = selection.Method
                    .Replace("Donor ISO9660", "Donor UDF", StringComparison.Ordinal)
                    .Replace("Donor Joliet pathname", "Donor UDF pathname", StringComparison.Ordinal);
                matches[entry.Path] = new SkeletonSourceMatch(
                    entry,
                    extractedPath,
                    string.Empty,
                    false,
                    method);
            }

            completed++;
            progress?.Report(new DicDonorProgress(
                completed,
                Math.Max(1, required.Length),
                $"Scanning donor UDF filesystem — {completed:N0}/{required.Length:N0}"));
        }

        SkeletonDonorRequirement[] mandatoryRequirements = (inspection.DonorRequirements ?? Array.Empty<SkeletonDonorRequirement>())
            .Where(requirement => requirement.BlocksResurrection)
            .ToArray();
        if (mandatoryRequirements.Length > 0)
            warnings.Add($"The recovery still has {mandatoryRequirements.Length:N0} mandatory raw/exact donor region(s); a UDF logical filesystem source cannot satisfy them.");

        return new DicDonorScanResult(
            donorPath,
            udf.SourceSectorSize,
            udf.VolumeIdentifier,
            false,
            true,
            false,
            false,
            false,
            0,
            0,
            0,
            mandatoryRequirements.Length == 0,
            files,
            matches,
            Array.Empty<DicJolietPaddingEvidence>(),
            warnings);
    }

    private static string BuildPrivateUdfExtractionPath(UdfImageFile file, int index)
    {
        string name = SanitizeHostName(Path.GetFileName(NormalizePath(file.Path)));
        return Path.Combine(
            IsoExtractionManifestService.PrivateDirectoryName,
            "files",
            $"UDF_{index:D8}_{name}");
    }

    private static async Task ExtractUdfFileAsync(
        UdfImageReader udf,
        string udfPath,
        string destination,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string partial = destination + ".partial";
        try
        {
            using Stream input = udf.OpenFile(udfPath);
            await using (var output = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(partial, destination, true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }
}
