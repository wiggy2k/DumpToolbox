using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed record DicDonorProgress(long Completed, long Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

public sealed record DicDonorExtent(
    uint ExtentLba,
    long DataLength,
    int ExtendedAttributeRecordLength = 0,
    int FileUnitSize = 0,
    int InterleaveGapSize = 0);

public sealed record DicDonorFile(
    string Path,
    uint ExtentLba,
    long DataLength,
    DateTimeOffset? RecordingTime = null,
    byte FileFlags = 0,
    int ExtendedAttributeRecordLength = 0,
    int FileUnitSize = 0,
    int InterleaveGapSize = 0,
    IReadOnlyList<DicDonorExtent>? Extents = null,
    uint DirectoryExtentLba = 0,
    int DirectoryRecordOffset = -1,
    int DirectoryRecordIndex = -1)
{
    public bool IsAssociated => (FileFlags & 0x04) != 0;
    public bool IsDirectory => (FileFlags & 0x02) != 0;
    // Only a non-empty Associated File payload is inherently unavailable from a
    // normal mounted filesystem. EARs affect bytes before the file payload and
    // interleaving affects placement, but both can still be handled when extracting
    // the ordinary file data from a donor image.
    public bool RequiresExactDonorSemantics => IsAssociated && DataLength > 0;
}

public sealed record DicDonorScanResult(
    string ImagePath,
    int SectorSize,
    string VolumeIdentifier,
    bool HasJoliet,
    bool PvdMatches,
    bool VolumeIdentifierMatches,
    bool SameDisc,
    int MetadataSectorsApplied,
    int RequiredPayloadsApplied,
    int OptionalExactnessRegionsApplied,
    bool DonorRequirementsSatisfied,
    IReadOnlyList<DicDonorFile> Files,
    IReadOnlyDictionary<string, SkeletonSourceMatch> Matches,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Treats a cooked 2048-byte ISO or raw 2352-byte BIN as a donor filesystem for
/// DiscImageCreator recovery. Primary ISO9660 records remain the physical reconstruction
/// authority. When a valid Joliet SVD is present, its namespace is also parsed so the ISO
/// Extractor can expose user-visible Joliet names and record their correspondence to the
/// primary records in the extraction manifest. A same-disc donor (exact PVD + volume label)
/// may donate its original primary ISO9660 metadata sectors. A non-matching donor is still
/// useful as a recursively searched pool of file payloads.
/// </summary>
public sealed partial class DicDonorImageService
{
    private const int CookedSectorSize = SkeletonResurrectionService.CookedSectorSize;
    private const int RawSectorSize = SkeletonResurrectionService.RawSectorSize;
    private const int SystemAreaSectors = 16;
    private static readonly byte[] Cd001 = Encoding.ASCII.GetBytes("CD001");

    public async Task<IsoExtractionResult> ExtractAllAsync(
        string imagePath,
        string outputDirectory,
        IProgress<DicDonorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Choose a 2048-byte ISO or 2352-byte BIN image.", nameof(imagePath));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Choose an extraction folder.", nameof(outputDirectory));

        string sourcePath = Path.GetFullPath(imagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source image not found.", sourcePath);

        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var warnings = new List<string>();

        progress?.Report(new DicDonorProgress(0, 1, "Reading ISO9660/Joliet filesystem"));
        await using var image = await DonorImageReader.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        DonorFilesystem filesystem = await ParseFilesystemAsync(image, cancellationToken).ConfigureAwait(false);
        if (filesystem.Pvd is null)
            throw new InvalidOperationException("The source image does not contain a readable ISO9660 Primary Volume Descriptor.");

        DicDonorFile[] fileRecords = filesystem.Files.Where(file => !file.IsDirectory).ToArray();
        DicDonorFile[] jolietFileRecords = filesystem.JolietFiles.Where(file => !file.IsDirectory).ToArray();
        var jolietByPrimary = new Dictionary<DicDonorFile, DicDonorFile>();
        foreach (DicDonorFile primary in fileRecords)
        {
            DicDonorFile? joliet = FindUnambiguousJolietRecord(primary, jolietFileRecords);
            if (joliet is not null)
                jolietByPrimary[primary] = joliet;
        }

        // One host-visible file is selected per visible pathname. For a Joliet disc the
        // preferred visible pathname is the proven Joliet path; otherwise it is the primary
        // ISO9660 path. Associated and colliding records stay in the private manifest area.
        var normalWinner = new HashSet<DicDonorFile>();
        foreach (IGrouping<string, DicDonorFile> group in fileRecords
                     .Where(file => !file.IsAssociated)
                     .GroupBy(file => NormalizePath(jolietByPrimary.TryGetValue(file, out DicDonorFile? joliet) ? joliet.Path : file.Path), StringComparer.OrdinalIgnoreCase))
        {
            DicDonorFile? visible = group.FirstOrDefault();
            if (visible is not null)
                normalWinner.Add(visible);
        }

        int associatedCount = fileRecords.Count(file => file.IsAssociated);
        int duplicateRecords = fileRecords.Length - normalWinner.Count - associatedCount;

        var manifest = new IsoExtractionManifest
        {
            SourceImageName = Path.GetFileName(sourcePath),
            SourceSectorSize = image.SectorSize,
            VolumeIdentifier = filesystem.VolumeIdentifier,
            PvdSha256 = Convert.ToHexString(SHA256.HashData(filesystem.Pvd)).ToLowerInvariant(),
            HasJoliet = filesystem.HasJoliet,
            VisibleNamespace = filesystem.HasJoliet ? "Joliet" : "ISO9660"
        };

        int extracted = 0;
        for (int i = 0; i < fileRecords.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DicDonorFile file = fileRecords[i];
            jolietByPrimary.TryGetValue(file, out DicDonorFile? jolietRecord);
            bool normalPath = normalWinner.Contains(file) && !file.IsAssociated;
            string visiblePath = jolietRecord?.Path ?? file.Path;
            string relativePath = normalPath
                ? BuildFilesystemExtractionPath(visiblePath)
                : BuildPrivateExtractionPath(file);
            string destination = Path.Combine(root, relativePath);

            await ExtractFileAsync(image, file, destination, cancellationToken).ConfigureAwait(false);
            manifest.Files.Add(new IsoExtractionManifestFile
            {
                IsoPath = file.Path,
                JolietPath = jolietRecord?.Path,
                ExtractedRelativePath = relativePath.Replace('\\', '/'),
                PrimaryDirectoryExtentLba = file.DirectoryExtentLba,
                PrimaryDirectoryRecordOffset = file.DirectoryRecordOffset,
                PrimaryDirectoryRecordIndex = file.DirectoryRecordIndex,
                JolietDirectoryExtentLba = jolietRecord?.DirectoryExtentLba,
                JolietDirectoryRecordOffset = jolietRecord?.DirectoryRecordOffset,
                JolietDirectoryRecordIndex = jolietRecord?.DirectoryRecordIndex,
                ExtentLba = file.ExtentLba,
                DataLength = file.DataLength,
                FileFlags = file.FileFlags,
                ExtendedAttributeRecordLength = file.ExtendedAttributeRecordLength,
                FileUnitSize = file.FileUnitSize,
                InterleaveGapSize = file.InterleaveGapSize,
                Extents = (file.Extents ?? Array.Empty<DicDonorExtent>()).ToList()
            });
            extracted++;
            progress?.Report(new DicDonorProgress(i + 1, Math.Max(1, fileRecords.Length), $"Extracting filesystem records — {i + 1:N0}/{fileRecords.Length:N0}"));
        }

        if (filesystem.HasJoliet)
            warnings.Add("Joliet detected. The visible extraction tree uses proven Joliet pathnames where they map unambiguously to primary ISO9660 records; the manifest preserves both namespaces and their directory-record ordering evidence.");
        if (associatedCount > 0)
            warnings.Add($"Preserved {associatedCount:N0} ISO9660 Associated File record(s) under '{IsoExtractionManifestService.PrivateDirectoryName}'. They are linked to their original primary/Joliet identity and extent by the manifest and should not be renamed.");
        if (duplicateRecords > 0)
            warnings.Add($"Preserved {duplicateRecords:N0} additional colliding filesystem record(s) in the private record area instead of allowing the host filesystem to collapse them.");
        if (image.SectorSize == CookedSectorSize)
            warnings.Add("This extraction came from a 2048-byte cooked ISO. It can supply ordinary/Form-1 filesystem payloads, but it cannot supply full 2324-byte Mode 2 Form 2 payloads if the DIC logs require them.");

        await IsoExtractionManifestService.SaveAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.Combine(root, IsoExtractionManifestService.ManifestFileName);
        progress?.Report(new DicDonorProgress(fileRecords.Length, Math.Max(1, fileRecords.Length), "Extraction complete"));

        return new IsoExtractionResult(
            sourcePath,
            root,
            manifestPath,
            image.SectorSize,
            filesystem.VolumeIdentifier,
            extracted,
            associatedCount,
            duplicateRecords,
            filesystem.HasJoliet,
            jolietByPrimary.Count,
            warnings);
    }

    private static string BuildFilesystemExtractionPath(string isoPath)
    {
        string[] parts = NormalizePath(isoPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeHostName)
            .ToArray();
        return parts.Length == 0 ? "_root_file" : Path.Combine(parts);
    }

    private static string BuildPrivateExtractionPath(DicDonorFile file)
    {
        string name = Path.GetFileName(NormalizePath(file.Path));
        name = SanitizeHostName(string.IsNullOrWhiteSpace(name) ? "payload.bin" : name);
        string recordIdentity = file.DirectoryRecordOffset >= 0
            ? $"DIR_{file.DirectoryExtentLba:D8}_OFF_{file.DirectoryRecordOffset:D8}_"
            : string.Empty;
        string fileName = $"{recordIdentity}LBA_{file.ExtentLba:D8}_FLAGS_{file.FileFlags:X2}_{name}";
        return Path.Combine(IsoExtractionManifestService.PrivateDirectoryName, "files", fileName);
    }

    private static DicDonorFile? FindUnambiguousJolietRecord(
        DicDonorFile primary,
        IReadOnlyList<DicDonorFile> jolietFiles)
    {
        DicDonorFile[] candidates = jolietFiles
            .Where(candidate => candidate.IsAssociated == primary.IsAssociated)
            .Where(candidate => candidate.IsDirectory == primary.IsDirectory)
            .Where(candidate => candidate.ExtentLba == primary.ExtentLba)
            .Where(candidate => candidate.DataLength == primary.DataLength)
            .Where(candidate => DonorExtentGeometryMatches(primary, candidate))
            .ToArray();

        if (candidates.Length == 1)
            return candidates[0];

        if (candidates.Length > 1 && primary.RecordingTime is DateTimeOffset primaryTime)
        {
            DicDonorFile[] timestampMatches = candidates
                .Where(candidate => candidate.RecordingTime is DateTimeOffset candidateTime && candidateTime.Equals(primaryTime))
                .ToArray();
            if (timestampMatches.Length == 1)
                return timestampMatches[0];
        }

        // Never invent a namespace correspondence when two Joliet records genuinely
        // share the same payload geometry. Those aliases remain represented by their
        // primary identities and can be disambiguated later from richer evidence.
        return null;
    }

    private static bool DonorExtentGeometryMatches(DicDonorFile left, DicDonorFile right)
    {
        IReadOnlyList<DicDonorExtent> leftExtents = left.Extents ?? Array.Empty<DicDonorExtent>();
        IReadOnlyList<DicDonorExtent> rightExtents = right.Extents ?? Array.Empty<DicDonorExtent>();
        if (leftExtents.Count == 0 && rightExtents.Count == 0)
            return true;
        if (leftExtents.Count != rightExtents.Count)
            return false;

        for (int i = 0; i < leftExtents.Count; i++)
        {
            DicDonorExtent a = leftExtents[i];
            DicDonorExtent b = rightExtents[i];
            if (a.ExtentLba != b.ExtentLba || a.DataLength != b.DataLength)
                return false;
        }
        return true;
    }

    private static string SanitizeHostName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
            builder.Append(invalid.Contains(ch) || ch == ':' ? '_' : ch);
        string result = builder.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }

    public Task<DicDonorScanResult> MatchAsync(
        SkeletonInspectionResult inspection,
        string donorImagePath,
        string cacheRoot,
        bool applySameDiscMetadata,
        IProgress<DicDonorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => MatchCoreAsync(
                inspection,
                donorImagePath,
                cacheRoot,
                applySameDiscMetadata,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private async Task<DicDonorScanResult> MatchCoreAsync(
        SkeletonInspectionResult inspection,
        string donorImagePath,
        string cacheRoot,
        bool applySameDiscMetadata,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(donorImagePath))
            throw new ArgumentException("Choose a 2048-byte ISO or 2352-byte BIN donor image.", nameof(donorImagePath));

        string donorPath = Path.GetFullPath(donorImagePath);
        if (!File.Exists(donorPath))
            throw new FileNotFoundException("Donor image not found.", donorPath);

        var warnings = new List<string>();
        progress?.Report(new DicDonorProgress(0, 1, "Opening donor image"));

        await using var donor = await DonorImageReader.OpenAsync(donorPath, cancellationToken).ConfigureAwait(false);
        DonorFilesystem filesystem = await ParseFilesystemAsync(donor, cancellationToken).ConfigureAwait(false);
        if (filesystem.Pvd is null)
            throw new InvalidOperationException("The donor image does not contain a readable ISO9660 Primary Volume Descriptor.");

        byte[]? targetPvd = await ReadTargetLogicalSectorAsync(inspection, SystemAreaSectors, cancellationToken).ConfigureAwait(false);
        bool pvdMatches = targetPvd is not null && targetPvd.AsSpan().SequenceEqual(filesystem.Pvd);
        bool volumeMatches = !string.IsNullOrWhiteSpace(filesystem.VolumeIdentifier) &&
                             filesystem.VolumeIdentifier.Equals(inspection.VolumeIdentifier, StringComparison.OrdinalIgnoreCase);
        bool sameDisc = pvdMatches && volumeMatches;

        // A cooked 2048-byte ISO can be extremely useful when restoring a raw 2352-byte
        // CD, but it must never be promoted to raw-sector evidence. It has already lost
        // sync/MSF/mode/subheader/EDC/ECC bytes (and cannot carry 2324-byte Form-2 sectors).
        // In that combination treat the image strictly as a filesystem/file-payload source,
        // even when its PVD and volume identifier prove that it came from the same disc.
        bool payloadOnlySource = inspection.ImageKind == SkeletonImageKind.Raw2352 &&
                                 donor.SectorSize == CookedSectorSize;
        bool exactDonorEligible = sameDisc && !payloadOnlySource;

        if (payloadOnlySource)
        {
            warnings.Add(
                "The selected source is a 2048-byte cooked ISO while the DIC target is a raw 2352-byte CD. " +
                "It will be used only as a recursive source of ISO9660 file payloads. " +
                "It will not supply primary metadata sectors, system/slack/exactness regions, raw framing, XA subheaders, EDC/ECC, audio, or Mode 2 Form 2 raw bytes. " +
                "A 2352-byte BIN is still required for any raw-sector exactness requirements.");
        }
        else if (!sameDisc)
        {
            warnings.Add(
                $"Donor PVD/volume identity does not exactly match the DIC reconstruction " +
                $"(PVD {(pvdMatches ? "matches" : "differs")}, volume label {(volumeMatches ? "matches" : "differs")}). " +
                "The donor will therefore be used only as a recursive pool of candidate files; its filesystem metadata will not be copied into the rebuilt disc.");
        }

        int metadataApplied = 0;
        if (exactDonorEligible && applySameDiscMetadata)
        {
            progress?.Report(new DicDonorProgress(0, filesystem.MetadataLbas.Count, "Applying original ISO9660 metadata"));
            metadataApplied = await ApplyMetadataAsync(
                inspection.SkeletonPath,
                donor,
                filesystem.MetadataLbas,
                inspection,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SkeletonDonorRequirement> donorRequirements =
            inspection.DonorRequirements ?? Array.Empty<SkeletonDonorRequirement>();
        SkeletonDonorRequirement[] mandatoryRequirements = donorRequirements
            .Where(requirement => requirement.BlocksResurrection)
            .ToArray();
        SkeletonDonorRequirement[] optionalExactnessRequirements = donorRequirements
            .Where(requirement => !requirement.BlocksResurrection)
            .ToArray();

        int requiredApplied = 0;
        int optionalApplied = 0;
        bool donorRequirementsSatisfied = mandatoryRequirements.Length == 0;

        if (mandatoryRequirements.Length > 0 && payloadOnlySource)
        {
            warnings.Add(
                $"This recovery still has {mandatoryRequirements.Length:N0} mandatory raw/exact donor region(s). " +
                "They were deliberately left unsatisfied because a cooked 2048-byte ISO is file-source evidence only for a raw-CD target. " +
                "Matched ordinary files from this ISO will still be queued; supply a matching 2352-byte BIN separately if those exact regions are needed.");
        }
        else if (mandatoryRequirements.Length > 0 && !sameDisc)
        {
            throw new InvalidOperationException(
                $"This DIC recovery contains {mandatoryRequirements.Length:N0} ISO 9660 region(s) that require an exact donor image. " +
                "The donor must match both the Primary Volume Descriptor and volume identifier before resurrection can continue.");
        }

        if (exactDonorEligible && mandatoryRequirements.Length > 0)
        {
            progress?.Report(new DicDonorProgress(0, mandatoryRequirements.Length, "Applying mandatory ISO9660 sectors from donor"));
            requiredApplied = await ApplyRequiredPayloadsAsync(
                inspection,
                donor,
                filesystem.Files,
                mandatoryRequirements,
                progress,
                cancellationToken,
                strict: true,
                warnings: warnings).ConfigureAwait(false);
            donorRequirementsSatisfied = requiredApplied == mandatoryRequirements.Length;
            if (!donorRequirementsSatisfied)
            {
                throw new InvalidOperationException(
                    $"The donor supplied only {requiredApplied:N0} of {mandatoryRequirements.Length:N0} required ISO 9660 payload region(s). " +
                    "Resurrection cannot continue with this donor.");
            }
        }

        if (optionalExactnessRequirements.Length > 0)
        {
            if (payloadOnlySource)
            {
                warnings.Add(
                    $"{optionalExactnessRequirements.Length:N0} optional exactness region(s) were not copied because a cooked ISO is file-source evidence only for a raw-CD target.");
            }
            else if (!sameDisc)
            {
                warnings.Add(
                    $"{optionalExactnessRequirements.Length:N0} optional exactness region(s) (system-area/slack/post-volume/synthesized metadata) were not copied because this donor is not an exact same-disc match.");
            }
            else
            {
                progress?.Report(new DicDonorProgress(0, optionalExactnessRequirements.Length, "Applying optional exactness sectors from donor"));
                optionalApplied = await ApplyRequiredPayloadsAsync(
                    inspection,
                    donor,
                    filesystem.Files,
                    optionalExactnessRequirements,
                    progress,
                    cancellationToken,
                    strict: false,
                    warnings: warnings).ConfigureAwait(false);
            }
        }

        Directory.CreateDirectory(cacheRoot);
        string donorCache = Path.Combine(cacheRoot, BuildDonorCacheId(donorPath));
        Directory.CreateDirectory(donorCache);

        JolietNamingProfile? targetNamingProfile = JolietNamingRuleService.ResolveForInspection(inspection, out IsoMasteringIdentity targetMasteringIdentity, out IReadOnlyList<string> namingWarnings);
        foreach (string namingWarning in namingWarnings)
            warnings.Add($"JolietNamingRules.ini warning: {namingWarning}");
        if (targetNamingProfile is not null)
            warnings.Add($"Joliet naming profile '{targetNamingProfile.Name}' selected for target mastering application '{targetMasteringIdentity.ApplicationId}'.");

        SkeletonContentEntry[] required = inspection.Entries
            .Where(e => e.CanRestore && e.RequiresSource && !e.IsEmpty)
            .ToArray();

        DicDonorFile[] donorPrimaryFiles = filesystem.Files.Where(file => !file.IsDirectory).ToArray();
        DicDonorFile[] donorJolietFiles = filesystem.JolietFiles.Where(file => !file.IsDirectory).ToArray();
        var jolietByPrimary = new Dictionary<DicDonorFile, DicDonorFile>();
        foreach (DicDonorFile primary in donorPrimaryFiles)
        {
            DicDonorFile? joliet = FindUnambiguousJolietRecord(primary, donorJolietFiles);
            if (joliet is not null)
                jolietByPrimary[primary] = joliet;
        }

        Dictionary<long, List<DicDonorFile>> bySize = donorPrimaryFiles
            .GroupBy(f => f.DataLength)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Resolve every stronger path/Joliet projection match before attempting the
        // final alias-family order fallback. This prevents an early ambiguous family
        // member from stealing a donor record that a later member can prove directly.
        var strongSelections = new Dictionary<string, DonorPayloadSelection>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonContentEntry candidateEntry in required)
        {
            DonorPayloadSelection? strong = FindStrongDonorPayloadMatch(candidateEntry, required, bySize, jolietByPrimary, targetNamingProfile);
            if (strong is not null)
                strongSelections[candidateEntry.Path] = strong;
        }

        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
        long total = Math.Max(1, required.LongLength);
        long completed = 0;

        foreach (SkeletonContentEntry entry in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DonorPayloadSelection? chosen = strongSelections.TryGetValue(entry.Path, out DonorPayloadSelection? strong)
                ? strong
                : FindOrderedAliasFamilyMatch(entry, required, donorPrimaryFiles, jolietByPrimary, strongSelections);
            DicDonorFile? selected = chosen?.File;
            string method = chosen?.Method ?? string.Empty;

            // There is deliberately no size-only fallback. The last-stage family-order
            // rule still requires a proven Joliet/ISO alias family, exact size/timestamp,
            // matching cardinality and deterministic filesystem order.
            if (selected is not null && donor.SectorSize == CookedSectorSize && entry.ContainsMode2Form2)
            {
                warnings.Add($"'{entry.Path}' includes Mode 2 Form 2 sectors. A 2048-byte ISO donor cannot contain the full 2324-byte Form 2 payload, so this entry was not accepted from the cooked donor.");
                selected = null;
            }

            if (selected is not null)
            {
                string extractedPath = BuildCachedSourcePath(donorCache, entry, selected);
                try
                {
                    if (!File.Exists(extractedPath) || new FileInfo(extractedPath).Length != entry.DataLength)
                    {
                        await ExtractFileAsync(donor, selected, extractedPath, cancellationToken).ConfigureAwait(false);
                    }

                    string? sourceRelativePath = null;
                    if (jolietByPrimary.TryGetValue(selected, out DicDonorFile? mappedJoliet))
                    {
                        sourceRelativePath = NormalizePath(mappedJoliet.Path);
                        if (string.IsNullOrWhiteSpace(method))
                            method = "Donor Joliet pathname -> DIC primary ISO9660 record + exact path+size";
                    }

                    matches[entry.Path] = new SkeletonSourceMatch(
                        entry,
                        extractedPath,
                        string.Empty,
                        false,
                        method,
                        SourceRelativePath: sourceRelativePath);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Form 2", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"'{selected.Path}' could not be extracted from the donor as a normal 2048-byte filesystem file: {ex.Message}");
                }
            }

            completed++;
            progress?.Report(new DicDonorProgress(completed, total, $"Scanning donor ISO9660 filesystem — {completed:N0}/{total:N0}"));
        }

        return new DicDonorScanResult(
            donorPath,
            donor.SectorSize,
            filesystem.VolumeIdentifier,
            filesystem.HasJoliet,
            pvdMatches,
            volumeMatches,
            sameDisc,
            metadataApplied,
            requiredApplied,
            optionalApplied,
            donorRequirementsSatisfied,
            filesystem.Files,
            matches,
            warnings);
    }

}
