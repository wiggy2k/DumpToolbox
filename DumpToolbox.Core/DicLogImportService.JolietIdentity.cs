using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    public Task<DicJolietNameUpdateResult> ApplyMatchedJolietNamesAsync(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ApplyMatchedJolietNamesCore(inspection, matches, sourceDirectory, cancellationToken),
            cancellationToken);
    }


    private static DicJolietNameUpdateResult ApplyMatchedJolietNamesCore(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (inspection.SourceKind != SkeletonSourceKind.DiscImageCreator ||
            (inspection.ImageKind != SkeletonImageKind.Raw2352 &&
             inspection.ImageKind != SkeletonImageKind.Cooked2048))
        {
            return new DicJolietNameUpdateResult(false, 0, 0, 0, "not-DIC", warnings);
        }

        // v0.1.9: an ISO9660-only disc must not be rejected for lacking Joliet pathname
        // evidence.  Establish that a real Joliet SVD exists before inspecting source
        // names; otherwise there is no supplementary namespace to reconstruct.
        if (!TryReadJolietSvdFromSkeleton(inspection, out long svdLba, out byte[]? svdPayload, cancellationToken))
            return new DicJolietNameUpdateResult(false, 0, 0, 0, "ISO9660-only", warnings);

        SkeletonContentEntry[] ordinaryEntries = inspection.Entries
            .Where(entry => entry.SpecialKind == SkeletonSpecialKind.None)
            .Where(entry => (entry.IsoFileFlags & (byte)IsoDirectoryRecordFlags.Associated) == 0)
            .OrderBy(entry => entry.ExtentLba)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ordinaryEntries.Length == 0)
            return new DicJolietNameUpdateResult(false, 0, 0, 0, "no ordinary files", warnings);

        bool matchesAlreadyContainMountedDiscNames = matches.Values.Any(match =>
            match.MatchMethod.Equals(MountedDiscJolietPathService.MatchMethod, StringComparison.Ordinal));
        IReadOnlyDictionary<string, string> mountedDiscJolietPaths = matchesAlreadyContainMountedDiscNames
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : MountedDiscJolietPathService.TryRead(sourceDirectory, cancellationToken);

        // Supplementary directory sectors are not present in DIC's usual log set.
        // Rebuild them only when every ordinary file has a trustworthy path identity:
        // either DIC logged a distinct long-name alias itself or a source match carries
        // a relative path that was validated against the primary ISO9660 record.
        var files = new List<DicFileRecord>(ordinaryEntries.Length);
        var directoryMetadata = ReadPrimaryDirectoryMetadata(inspection, cancellationToken);
        int matchedUsed = 0;
        int sourcePathsUsed = 0;
        int dicAliasesUsed = 0;
        int zeroLengthPrimaryNameFallbacks = 0;
        int supplementaryOnlyZeroLengthAliases = 0;
        var zeroLengthPrimaryAnchors = new List<(string PrimaryPath, DicFileRecord Record)>();
        var missingNameEvidence = new List<string>();

        foreach (SkeletonContentEntry entry in ordinaryEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string isoPath = SelectDicIsoPath(entry);
            string dicJolietPath = SelectDicJolietPath(entry);
            string candidatePath = dicJolietPath;
            bool hasDicLongAlias = !NormalizeIsoPath(dicJolietPath)
                .Equals(NormalizeIsoPath(isoPath), StringComparison.OrdinalIgnoreCase) &&
                GetDicLoggedAliases(entry).Length > 1;

            if (hasDicLongAlias)
            {
                dicAliasesUsed++;
            }
            else if (matches.TryGetValue(entry.Path, out SkeletonSourceMatch? match) && match is not null)
            {
                matchedUsed++;
                string? relative = match.SourceRelativePath;
                string mountedDiscJolietPath = string.Empty;
                bool matchComesFromSelectedSource = SourcePathIsInsideDirectory(match.SourcePath, sourceDirectory);
                bool mountedDiscPathResolved = matchComesFromSelectedSource &&
                    !string.IsNullOrWhiteSpace(relative) &&
                    MountedDiscJolietPathService.TryResolveJolietPath(
                        relative,
                        mountedDiscJolietPaths,
                        out mountedDiscJolietPath);
                if (mountedDiscPathResolved)
                {
                    candidatePath = mountedDiscJolietPath;
                    sourcePathsUsed++;
                }
                else if (!string.IsNullOrWhiteSpace(relative) && MatchMethodTrustsRelativePath(match.MatchMethod))
                {
                    string normalizedRelative = NormalizeIsoPath(relative);
                    // Later-stage matchers can prove source identity even when the long Joliet
                    // name cannot project back to an unrelated primary ISO9660 short alias.
                    // Once that identity has been proven, preserve the recovered source-relative
                    // pathname as authoritative Joliet evidence instead of rejecting it again.
                    bool relativePathMatches = MatchMethodProvesJolietIdentity(match.MatchMethod) ||
                                               SourceJolietPathMatchesPrimaryEntry(normalizedRelative, isoPath);

                    // v0.1.12: the collision-alias matcher is deliberately stricter than
                    // the ordinary Joliet projection matcher: it requires parent-path
                    // compatibility, a recognised ISO9660 collision family, exact file
                    // size, and bidirectional uniqueness. Once that stronger matcher has
                    // accepted a source file, its long relative pathname is trustworthy
                    // Joliet evidence even though the ordinary projection test cannot map
                    // e.g. MAR20092.CAB back to Mar2009_d3dx10_41_x86.cab.
                    if (!relativePathMatches && MatchMethodTrustsCollisionAliasPath(match.MatchMethod))
                        relativePathMatches = SourceJolietPathMatchesPrimaryCollisionAlias(normalizedRelative, isoPath);

                    if (relativePathMatches)
                    {
                        candidatePath = normalizedRelative;
                        sourcePathsUsed++;
                    }
                    else
                    {
                        missingNameEvidence.Add(entry.Path);
                    }
                }
                else
                {
                    missingNameEvidence.Add(entry.Path);
                }
            }
            else if (entry.DataLength == 0 && IsSafeZeroLengthPrimaryJolietFallback(isoPath))
            {
                // A genuine zero-byte file has no payload to match, so an extracted
                // source tree may legitimately have no counterpart from which to learn
                // a Joliet spelling.  When the primary path is already a clean name
                // (no ISO short-name '~' aliases), use it as a conservative Joliet
                // fallback rather than blocking reconstruction of the entire tree.
                candidatePath = NormalizeIsoPath(isoPath);
                zeroLengthPrimaryNameFallbacks++;
            }
            else
            {
                missingNameEvidence.Add(entry.Path);
            }

            PrimaryDirectoryMetadata? primaryRecordMetadata = directoryMetadata.TryGetValue(NormalizeIsoPath(isoPath), out PrimaryDirectoryMetadata? foundPrimaryRecord)
                ? foundPrimaryRecord
                : null;
            byte[]? primarySystemUse = primaryRecordMetadata?.SystemUse;
            byte[]? primaryRawRecordingTime = primaryRecordMetadata?.RawRecordingTime;

            var reconstructedFile = new DicFileRecord(
                candidatePath,
                entry.IsoRecordExtentLba ?? entry.ExtentLba,
                entry.DataLength,
                0,
                0,
                0,
                entry.PathAliases,
                entry.RecordingTime,
                (IsoDirectoryRecordFlags)entry.IsoFileFlags,
                Sequence: primaryRecordMetadata?.PrimaryRecordOrder ?? int.MaxValue,
                OriginalPath: NormalizeIsoPath(isoPath),
                SystemUse: primarySystemUse,
                RawRecordingTime: primaryRawRecordingTime);
            files.Add(reconstructedFile);
            if (entry.DataLength == 0)
                zeroLengthPrimaryAnchors.Add((NormalizeIsoPath(isoPath), reconstructedFile));
        }

        if (missingNameEvidence.Count > 0)
        {
            string sample = string.Join(", ", missingNameEvidence.Take(8));
            warnings.Add(
                $"Joliet metadata was not synthesized because {missingNameEvidence.Count:N0} ordinary file(s) do not yet have a trustworthy Joliet pathname. " +
                "Supply/match the files using the Joliet filesystem names and directory layout; primary ISO9660 short names remain authoritative for extents and payload placement." +
                (sample.Length > 0 ? $" First unresolved Joliet identities: {sample}." : string.Empty));
            return new DicJolietNameUpdateResult(false, matchedUsed, dicAliasesUsed, sourcePathsUsed, "incomplete Joliet names", warnings);
        }

        // v0.1.29: a supplementary/Joliet tree can legitimately contain more
        // zero-byte file names than the primary ISO9660 tree.  A zero-byte file has
        // no payload/hash with which DIC can distinguish aliases, so the primary
        // import may collapse several Joliet-only names onto one zero-length primary
        // record.  Preserve additional zero-byte names from the supplied source tree
        // only when their parent directory maps uniquely to DIC primary metadata and
        // that primary directory has exactly one proven zero-length extent.  The extra
        // names then safely share that same zero-length extent; no payload bytes move.
        if (Directory.Exists(sourceDirectory) && zeroLengthPrimaryAnchors.Count > 0)
        {
            static string ParentIsoPath(string value)
            {
                string normalized = NormalizeIsoPath(value);
                int slash = normalized.LastIndexOf('/');
                return slash <= 0 ? "/" : normalized[..slash];
            }

            string[] sourceZeroFiles;
            try
            {
                sourceZeroFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                    .Where(path => new FileInfo(path).Length == 0)
                    .Select(path => NormalizeIsoPath(Path.GetRelativePath(sourceDirectory, path)))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                sourceZeroFiles = Array.Empty<string>();
            }

            foreach (string sourceZeroPath in sourceZeroFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If the logical record already exists, retain the exact source/Joliet
                // casing rather than creating a second case-only duplicate.
                int existingIndex = files.FindIndex(file =>
                    file.DataLength == 0 && file.Path.Equals(sourceZeroPath, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    if (!files[existingIndex].Path.Equals(sourceZeroPath, StringComparison.Ordinal))
                        files[existingIndex] = files[existingIndex] with { Path = sourceZeroPath };
                    continue;
                }

                string sourceParent = ParentIsoPath(sourceZeroPath);
                var parentAnchors = zeroLengthPrimaryAnchors
                    .Where(anchor => SourceJolietPathMatchesPrimaryEntry(sourceParent, ParentIsoPath(anchor.PrimaryPath), leafIsFile: false))
                    .ToArray();
                if (parentAnchors.Length == 0)
                    continue;

                long[] extents = parentAnchors.Select(anchor => anchor.Record.ExtentLba).Distinct().ToArray();
                if (extents.Length != 1)
                    continue;

                DicFileRecord template = parentAnchors
                    .Select(anchor => anchor.Record)
                    .OrderBy(record => record.Sequence)
                    .First();

                files.Add(template with
                {
                    Path = sourceZeroPath,
                    OriginalPath = template.OriginalPath ?? template.Path,
                    Sequence = int.MaxValue,
                    SupplementaryOnlyZeroLengthAlias = true
                });
                supplementaryOnlyZeroLengthAliases++;
            }
        }

        if (supplementaryOnlyZeroLengthAliases > 0)
        {
            warnings.Add(
                $"Recovered {supplementaryOnlyZeroLengthAliases:N0} supplementary-only zero-length file name(s) from the supplied Joliet source tree by mapping them to a unique DIC-proven zero-length primary extent in the same directory; v0.1.30 inserts only those aliases into Joliet identifier position while preserving all pre-existing directory-record order.");
        }

        long volumeSpaceSize = inspection.SectorCount;
        var metadata = new Dictionary<long, byte[]> { [svdLba] = (byte[])svdPayload!.Clone() };
        var attemptWarnings = new List<string>();
        DateTimeOffset defaultRecordingTime = TryReadIsoDirectoryTimestamp(svdPayload!.AsSpan(174, 7), out DateTimeOffset rootTime)
            ? rootTime
            : TryReadVolumeDescriptorTimestamp(svdPayload!, out DateTimeOffset svdTime)
              ? svdTime
              : files.Select(file => file.RecordingTime).FirstOrDefault(value => value is not null)
                ?? new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Preserve the original volDesc primary path-table evidence for the Joliet
        // allocator.  Earlier versions created this temporary volume with an empty
        // PrimaryPathTableRecords collection, which made the path-table-paired
        // allocator impossible to prove even though the import stage had already
        // parsed and used those exact records to synthesize the primary path table.
        // HashPath is the DIC volDesc path for DIC inspections.
        DicVolumeInfo originalVolumeEvidence = ParseVolumeDescription(inspection.HashPath, cancellationToken);
        var volume = new DicVolumeInfo(
            inspection.VolumeIdentifier,
            volumeSpaceSize,
            files,
            Array.Empty<DicFileRecord>(),
            new HashSet<long>(),
            defaultRecordingTime,
            originalVolumeEvidence.PrimaryPathTableLba,
            originalVolumeEvidence.PrimaryPathTableSize,
            originalVolumeEvidence.PrimaryPathTableRecords,
            originalVolumeEvidence.PathsReconstructedFromIdentifiers,
            inspection.DicSupplementaryDirectoryHints ?? originalVolumeEvidence.SupplementaryDirectoryHints,
            originalVolumeEvidence.PrimaryDescriptorVolumeSpaceSizes,
            originalVolumeEvidence.SupplementaryDescriptorVolumeSpaceSizes);

        string description = sourcePathsUsed > 0
            ? "validated matched Joliet source paths, with DIC primary ISO9660 extents/sizes/timestamps/flags and fresh supplementary records"
            : "DIC-supplied long-name aliases";
        CeQuadratLinkTableContext? ceQuadratLinkTable = TryReadCeQuadratLinkTableContext(inspection, cancellationToken);
        if (!TrySynthesizeJolietMetadata(volume, metadata, attemptWarnings, description, directoryMetadata, ceQuadratLinkTable))
        {
            warnings.AddRange(attemptWarnings);
            warnings.Add("The reconstructed Joliet names would not fit safely in the original supplementary metadata area, so it was left unchanged.");
            return new DicJolietNameUpdateResult(false, matchedUsed, dicAliasesUsed, sourcePathsUsed, "kept existing tree", warnings);
        }

        // Exact complete Main Channel sectors recovered from the original DIC mainInfo
        // are stronger evidence than any synthesized supplementary metadata. Never allow
        // a later source tree (which may come from another pressing) to overwrite them.
        HashSet<long> exactMainInfo = inspection.DicExactMainInfoLbas?.ToHashSet() ?? new HashSet<long>();

        // v0.1.42: an LBA appearing in the mainInfo "complete sector" index is not by
        // itself sufficient reason to suppress stronger SVD structural evidence.  Some
        // DIC logs expose a complete-looking zero/placeholder Main Channel dump for an
        // LBA that the preserved Joliet SVD simultaneously declares as a path-table
        // location. Unreal Gold demonstrates this at its supplementary Type-L/Type-M
        // path tables: blindly trusting the index removed the correctly synthesized
        // tables and left the sectors zero.
        //
        // Preserve an overlapping mainInfo sector only when the current skeleton really
        // contains non-zero logical user data there.  A zero logical payload cannot be
        // the original contents of a declared, non-empty Joliet path table, so in that
        // conflict the SVD + validated source-tree reconstruction is the stronger and
        // internally consistent evidence.
        var zeroExactMainInfoOverrides = new List<long>();
        var nonZeroExactMainInfoOverlaps = new List<long>();
        if (exactMainInfo.Count > 0)
        {
            using var exactStream = new FileStream(
                inspection.SkeletonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.RandomAccess);
            var rawSector = new byte[RawSectorSize];
            var logicalPayload = new byte[CookedSectorSize];

            foreach (long lba in metadata.Keys.Where(lba => lba != svdLba && exactMainInfo.Contains(lba)).OrderBy(lba => lba))
            {
                Array.Clear(logicalPayload);
                bool readable = TryReadLogical2048(exactStream, inspection, lba, rawSector, logicalPayload, cancellationToken);
                bool hasNonZeroPayload = readable && logicalPayload.Any(value => value != 0);
                if (hasNonZeroPayload)
                    nonZeroExactMainInfoOverlaps.Add(lba);
                else
                    zeroExactMainInfoOverrides.Add(lba);
            }
        }

        if (nonZeroExactMainInfoOverlaps.Count > 0)
        {
            foreach (long lba in nonZeroExactMainInfoOverlaps)
                metadata.Remove(lba);

            warnings.Add(
                $"JOLIET: Preserved {nonZeroExactMainInfoOverlaps.Count:N0} exact original mainInfo sector(s) that overlap the generated supplementary tree " +
                $"(first LBA {nonZeroExactMainInfoOverlaps[0]:N0}); synthesized Joliet was not allowed to overwrite non-zero original-pressing DIC evidence.");
        }

        if (zeroExactMainInfoOverrides.Count > 0)
        {
            warnings.Add(
                $"JOLIET: Ignored {zeroExactMainInfoOverrides.Count:N0} zero/placeholder mainInfo overlap(s) because the preserved SVD and validated Joliet tree require generated supplementary metadata there " +
                $"(first LBA {zeroExactMainInfoOverrides[0]:N0}).");
        }

        // Keep the primary ISO9660 filesystem completely isolated from the
        // supplementary Joliet rewrite. Discover the primary metadata directly
        // from the current raw image, snapshot it byte-for-byte, and reject any
        // generated Joliet layout that would overlap it.
        HashSet<long> primaryIsoMetadata = DiscoverPrimaryIsoMetadataLbas(inspection, cancellationToken);
        long[] overlap = metadata.Keys
            .Where(lba => lba != svdLba && primaryIsoMetadata.Contains(lba))
            .OrderBy(lba => lba)
            .ToArray();
        if (overlap.Length > 0)
        {
            warnings.Add(
                $"The generated Joliet tree would overlap {overlap.Length:N0} primary ISO9660 metadata sector(s) " +
                $"(first overlap LBA {overlap[0]:N0}); the update was rejected so the primary ISO tree remains untouched.");
            return new DicJolietNameUpdateResult(false, matchedUsed, dicAliasesUsed, sourcePathsUsed, "overlap rejected", warnings);
        }

        Dictionary<long, byte[]> primarySnapshot = SnapshotRawSectors(inspection, primaryIsoMetadata, cancellationToken);
        PatchRawMetadataSectors(inspection, metadata, cancellationToken);
        RestoreRawSectors(inspection, primarySnapshot, cancellationToken);

        warnings.Add(
            $"Primary ISO9660 metadata was preserved byte-for-byte while Joliet metadata was reconstructed ({primarySnapshot.Count:N0} protected filesystem-metadata sector(s)).");
        if (sourcePathsUsed > 0)
            warnings.Add($"Used {sourcePathsUsed:N0} validated Joliet source pathname(s) to recover supplementary names/casing while retaining DIC primary extents and sizes.");
        if (dicAliasesUsed > 0)
            warnings.Add($"Used DIC-supplied long-name aliases for {dicAliasesUsed:N0} file pathname(s).");
        if (zeroLengthPrimaryNameFallbacks > 0)
            warnings.Add(
                $"Used the primary ISO9660 pathname as a conservative Joliet-name fallback for {zeroLengthPrimaryNameFallbacks:N0} zero-length file(s) that had no source payload/pathname. " +
                "This fallback is used only when the primary pathname contains no '~' short-name alias.");
        warnings.AddRange(attemptWarnings);

        return new DicJolietNameUpdateResult(
            true,
            matchedUsed,
            dicAliasesUsed,
            sourcePathsUsed,
            "validated Joliet source paths -> DIC primary ISO records",
            warnings);
    }

    private static bool SourcePathIsInsideDirectory(string sourcePath, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(sourceDirectory))
            return false;

        try
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(sourceDirectory), Path.GetFullPath(sourcePath));
            return !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeZeroLengthPrimaryJolietFallback(string isoPath)
    {
        string normalized = NormalizeIsoPath(isoPath);
        string[] components = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
            return false;

        // A '~' component is explicit evidence that the primary name may be a
        // generated short alias, so it is not trustworthy enough to stand in for
        // an unknown Joliet name.  Clean primary names such as ALONE_CD1.DAT are
        // safe conservative fallbacks for zero-byte entries with no source payload.
        return components.All(component => !component.Contains('~'));
    }

    internal static bool SourceJolietPathMatchesPrimaryEntry(string sourcePath, string isoPath, bool leafIsFile = true)
    {
        string[] source = NormalizeIsoPath(sourcePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = NormalizeIsoPath(isoPath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (source.Length != target.Length || source.Length == 0)
            return false;

        for (int i = 0; i < source.Length; i++)
        {
            bool isFile = leafIsFile && i == source.Length - 1;
            string sourceComponent = Regex.Replace(source[i], @";\d+$", string.Empty);
            string targetComponent = Regex.Replace(target[i], @";\d+$", string.Empty);
            if (!JolietComponentMatchesPrimaryComponent(sourceComponent, targetComponent, isFile))
                return false;
        }
        return true;
    }


    private static bool SourceJolietPathMatchesPrimaryCollisionAlias(string sourcePath, string isoPath)
    {
        string[] source = NormalizeIsoPath(sourcePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = NormalizeIsoPath(isoPath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (source.Length != target.Length || source.Length == 0)
            return false;

        // Parent directories must still project normally. Only the file leaf is allowed
        // to use the numbered ISO9660 collision discriminator.
        for (int i = 0; i < source.Length - 1; i++)
        {
            string sourceComponent = Regex.Replace(source[i], @";\d+$", string.Empty);
            string targetComponent = Regex.Replace(target[i], @";\d+$", string.Empty);
            if (!JolietComponentMatchesPrimaryComponent(sourceComponent, targetComponent, isFile: false))
                return false;
        }

        string sourceLeaf = Regex.Replace(source[^1].Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        string targetLeaf = Regex.Replace(target[^1].Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        string projected = ProjectJolietNameToIsoLevel1(sourceLeaf, isFile: true);

        static (string Stem, string Extension) SplitFileComponent(string value)
        {
            int dot = value.LastIndexOf('.');
            return dot > 0 && dot < value.Length - 1
                ? (value[..dot], value[(dot + 1)..])
                : (value, string.Empty);
        }

        (string projectedStem, string projectedExtension) = SplitFileComponent(projected);
        (string targetStem, string targetExtension) = SplitFileComponent(targetLeaf.ToUpperInvariant());
        if (!projectedExtension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase))
            return false;
        if (projectedStem.Equals(targetStem, StringComparison.OrdinalIgnoreCase))
            return false;

        int commonPrefixLength = 0;
        int comparableLength = Math.Min(projectedStem.Length, targetStem.Length);
        while (commonPrefixLength < comparableLength &&
               char.ToUpperInvariant(projectedStem[commonPrefixLength]) == char.ToUpperInvariant(targetStem[commonPrefixLength]))
        {
            commonPrefixLength++;
        }

        if (commonPrefixLength == 0 ||
            commonPrefixLength >= projectedStem.Length ||
            commonPrefixLength >= targetStem.Length)
        {
            return false;
        }

        string displacedProjection = projectedStem[commonPrefixLength..];
        string discriminator = targetStem[commonPrefixLength..];
        return displacedProjection.Length == discriminator.Length &&
               displacedProjection.All(ch => ch == '_') &&
               discriminator.All(char.IsDigit) &&
               discriminator[0] >= '2';
    }

    private static bool JolietComponentMatchesPrimaryComponent(string sourceComponent, string targetComponent, bool isFile)
    {
        string source = sourceComponent.Normalize(NormalizationForm.FormC);
        string target = targetComponent.Normalize(NormalizationForm.FormC);

        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;
        if (ProjectJolietNameToIsoLevel1(source, isFile).Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;
        if (ProjectJolietNameToIsoLevel2(source, isFile).Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;
        if (ProjectJolietNameByElidingPunctuation(source, isFile).Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;
        if (ProjectJolietNameByRemovingSeparators(source, isFile)
            .Equals(ProjectJolietNameByRemovingSeparators(target, isFile), StringComparison.OrdinalIgnoreCase))
            return true;
        return JolietNameMatchesNumericShortAlias(source, target, isFile);
    }

    private static bool JolietNameMatchesNumericShortAlias(string source, string target, bool isFile)
    {
        static string KeepShortNameCharacters(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string sourceStem = source;
        string sourceExtension = string.Empty;
        string targetStem = target;
        string targetExtension = string.Empty;
        if (isFile)
        {
            int sourceDot = source.LastIndexOf('.');
            if (sourceDot > 0 && sourceDot < source.Length - 1)
            {
                sourceStem = source[..sourceDot];
                sourceExtension = source[(sourceDot + 1)..];
            }
            int targetDot = target.LastIndexOf('.');
            if (targetDot > 0 && targetDot < target.Length - 1)
            {
                targetStem = target[..targetDot];
                targetExtension = target[(targetDot + 1)..];
            }
        }

        // Some mastering tools encode a collision short-name suffix as _N rather
        // than the DOS-style ~N.  Cumhuriyet Bonus Disc is one such example:
        // "3D_Modeller" -> "3D_MOD_1" and long media filenames similarly
        // become PREFIX_1.  Accept either delimiter, while leaving exact size/full
        // path/reverse-uniqueness as the authority for source matching.
        // ISO9660/DOS-style short names may retain punctuation such as '&' in the
        // six-character prefix (for example Sam& Shara -> SAM&SH~1). Keep the
        // accepted set aligned with IsIso83Character, excluding '~' because it is
        // the numeric-alias delimiter here.
        Match match = Regex.Match(
            targetStem,
            @"^(?<prefix>[A-Z0-9_$%\-@!#&(){}^`']{1,6})(?:~|_)(?<n>[1-9][0-9]*)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        string normalizedSourceStem = KeepShortNameCharacters(sourceStem);
        string prefix = match.Groups["prefix"].Value.ToUpperInvariant();
        string comparableSourceStem = normalizedSourceStem.Replace("_", string.Empty, StringComparison.Ordinal);
        string comparablePrefix = KeepShortNameCharacters(prefix).Replace("_", string.Empty, StringComparison.Ordinal);
        if (comparablePrefix.Length == 0 ||
            comparableSourceStem.Length < comparablePrefix.Length ||
            !comparableSourceStem.StartsWith(comparablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!isFile)
            return true;

        string normalizedSourceExtension = KeepShortNameCharacters(sourceExtension);
        string normalizedTargetExtension = KeepShortNameCharacters(targetExtension);
        if (normalizedTargetExtension.Length == 0)
            return normalizedSourceExtension.Length == 0;

        string expectedExtension = normalizedSourceExtension.Length <= 3
            ? normalizedSourceExtension
            : normalizedSourceExtension[..3];
        return expectedExtension.Equals(normalizedTargetExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectJolietNameToIsoLevel1(string value, bool isFile)
    {
        return ProjectJolietNameReplacingPunctuation(value, isFile, isFile ? 8 : 8, 3);
    }

    private static string ProjectJolietNameToIsoLevel2(string value, bool isFile)
    {
        return ProjectJolietNameReplacingPunctuation(value, isFile, isFile ? 27 : 31, 3);
    }

    private static string ProjectJolietNameReplacingPunctuation(string value, bool isFile, int stemLimit, int extensionLimit)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string NormalizePart(string part, int maxLength)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                bool allowed = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
                builder.Append(allowed ? ch : '_');
            }
            string result = builder.ToString();
            return result.Length <= maxLength ? result : result[..maxLength];
        }

        string projectedStem = NormalizePart(stem, stemLimit);
        if (!isFile || extension.Length == 0)
            return projectedStem;
        string projectedExtension = NormalizePart(extension, extensionLimit);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string ProjectJolietNameByElidingPunctuation(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string KeepDCharacters(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string projectedStem = KeepDCharacters(stem);
        if (!isFile || extension.Length == 0)
            return projectedStem;
        string projectedExtension = KeepDCharacters(extension);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string ProjectJolietNameByRemovingSeparators(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string KeepAlphaNumeric(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string projectedStem = KeepAlphaNumeric(stem);
        if (!isFile || extension.Length == 0)
            return projectedStem;
        string projectedExtension = KeepAlphaNumeric(extension);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static bool TryResolvePrimaryDirectoryMetadata(
        string jolietPath,
        IReadOnlyDictionary<string, PrimaryDirectoryMetadata> primaryDirectoryMetadata,
        out PrimaryDirectoryMetadata metadata)
    {
        string normalized = NormalizeIsoPath(jolietPath);
        if (primaryDirectoryMetadata.TryGetValue(normalized, out metadata!))
            return true;

        var matches = primaryDirectoryMetadata
            .Where(pair => SourceJolietPathMatchesPrimaryEntry(normalized, pair.Key, leafIsFile: false))
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
        {
            metadata = matches[0].Value;
            return true;
        }

        metadata = default!;
        return false;
    }

    private static string SelectDicIsoPath(SkeletonContentEntry entry)
    {
        string[] aliases = GetDicLoggedAliases(entry);
        // Prefer the shortest/most DOS-like path for the primary ISO identity.
        return aliases
            .OrderBy(DicIsoPathScore)
            .ThenBy(path => path.Length)
            .FirstOrDefault() ?? NormalizeIsoPath(entry.Path);
    }

    private static string SelectDicJolietPath(SkeletonContentEntry entry)
    {
        string[] aliases = GetDicLoggedAliases(entry);
        // Prefer a non-8.3, mixed/lower-case, longer pathname when DIC logged one.
        return aliases
            .OrderByDescending(PathQuality)
            .ThenByDescending(path => path.Length)
            .FirstOrDefault() ?? NormalizeIsoPath(entry.Path);
    }

    private static string[] GetDicLoggedAliases(SkeletonContentEntry entry)
    {
        return new[] { entry.Path }
            .Concat(entry.PathAliases ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeIsoPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int DicIsoPathScore(string path)
    {
        string[] components = NormalizeIsoPath(path)
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        int score = 0;
        foreach (string component in components)
        {
            string clean = Regex.Replace(component, @";\d+$", string.Empty);
            if (!LooksLikeIso83Name(clean)) score += 100;
            if (clean.Any(char.IsLower)) score += 10;
            score += clean.Length;
        }
        return score;
    }

    private static bool LooksLikeIso83Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string name = value;
        int dot = name.LastIndexOf('.');
        string stem = dot >= 0 ? name[..dot] : name;
        string ext = dot >= 0 ? name[(dot + 1)..] : string.Empty;
        if (stem.Length > 8 || ext.Length > 3) return false;
        return stem.All(IsIso83Character) && ext.All(IsIso83Character);
    }

    private static bool IsIso83Character(char ch)
    {
        if (char.IsLetterOrDigit(ch)) return true;
        return ch is '_' or '~' or '$' or '%' or '-' or '@' or '!' or '#' or '&' or '(' or ')' or '{' or '}' or '^' or '`' or '\'';
    }
    private static string GetIsoFilename(string path)
    {
        string normalized = NormalizeIsoPath(path);
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized.TrimStart('/') : normalized[(slash + 1)..];
    }
    internal static bool MatchMethodTrustsRelativePath(string method)
        => method.Contains("Joliet", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("ISO9660 exact relative path+filename+size", StringComparison.OrdinalIgnoreCase) ||
           MatchMethodProvesJolietIdentity(method);

    // These matchers only succeed after they have uniquely established a source-file
    // identity in the correct parent directory. They therefore also prove the recovered
    // source-relative pathname for Joliet synthesis, even when the primary ISO9660 short
    // alias bears no reversible textual relationship to the long filename.
    private static bool MatchMethodProvesJolietIdentity(string method)
        => method.Equals("Donor Joliet pathname -> DIC primary ISO9660 record + exact path+size", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("Same-directory exact size + unique DIC recording timestamp", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("Same-directory exact size + DIC-proven ordinal Joliet extent order", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("Same-directory exact size + DIC-proven local extent/name bracket", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("Same-directory exact size + proven numeric alias identity across sibling extensions", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator WINDOWS_NT_HASHED_83_EXACT", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator WINDOWS_NT_HASHED_83_PATH_CHAIN_EXACT", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator WINDOWS_NT_HASHED_83_FROM_PROVEN_DISC_PROFILE", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator PROVEN_PARENT_RESCAN_SIZE_TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator PROVEN_PARENT_STRICT_ALIAS_SIZE_TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator RESIDUAL_MUTUAL_UNIQUE_SIZE_TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator ZERO_BASED_TERMINAL_ORDINAL_FAMILY", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator AFFINE_ALIAS_INDEX_FROM_PROVEN_FAMILY", StringComparison.OrdinalIgnoreCase) ||
           method.Equals("DICSimulator LEXICAL_ALIAS_RANK_FROM_PROVEN_FAMILY", StringComparison.OrdinalIgnoreCase);

    private static bool MatchMethodTrustsCollisionAliasPath(string method)
        => method.Equals(
               "Joliet collision alias -> DIC primary ISO9660 record + exact unique size",
               StringComparison.OrdinalIgnoreCase) ||
           MatchMethodProvesJolietIdentity(method);
    private static bool TryReadJolietSvdFromSkeleton(
        SkeletonInspectionResult inspection,
        out long svdLba,
        out byte[]? payload,
        CancellationToken cancellationToken)
    {
        svdLba = -1;
        payload = null;
        long scanEnd = Math.Min(inspection.SectorCount, 64);
        byte[] sector = new byte[RawSectorSize];
        byte[] candidate = new byte[CookedSectorSize];

        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        for (long sectorIndex = 0; sectorIndex < scanEnd; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long lba = inspection.BaseLba + sectorIndex;
            if (!TryReadLogical2048(stream, inspection, lba, sector, candidate, cancellationToken))
                continue;

            if (!IsJolietSupplementaryDescriptor(candidate))
                continue;

            svdLba = lba;
            payload = (byte[])candidate.Clone();
            return true;
        }

        return false;
    }

    private static CeQuadratLinkTableContext? TryReadCeQuadratLinkTableContext(
        SkeletonInspectionResult inspection,
        CancellationToken cancellationToken)
    {
        // CeQuadrat's private bridge has only been characterized on raw CD images.
        // Cooked DVD recovery does not need it and must not interpret 2048-byte sectors
        // using raw-sector offsets.
        if (inspection.ImageKind != SkeletonImageKind.Raw2352)
            return null;

        byte[] sector = new byte[RawSectorSize];
        byte[]? pvd = null;
        long pvdLba = -1;
        long terminatorLba = -1;

        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            256 * 1024,
            FileOptions.RandomAccess);

        long scanEnd = Math.Min(inspection.SectorCount, 64);
        for (long sectorIndex = 0; sectorIndex < scanEnd; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = sectorIndex * RawSectorSize;
            ReadExactly(stream, sector, cancellationToken);
            int userOffset = sector[15] == 1 ? 16 : sector[15] == 2 ? 24 : -1;
            if (userOffset < 0)
                continue;

            ReadOnlySpan<byte> payload = sector.AsSpan(userOffset, CookedSectorSize);
            long absoluteLba = inspection.BaseLba + sectorIndex;
            if (pvd is null && IsPrimaryVolumeDescriptor(payload))
            {
                pvd = payload.ToArray();
                pvdLba = absoluteLba;
            }
            if (IsVolumeDescriptorTerminator(payload))
                terminatorLba = absoluteLba;
        }

        if (pvd is null || pvdLba < 0 || terminatorLba < 0)
            return null;

        string preparer = ReadIsoAsciiField(pvd, 446, 128);
        if (!preparer.StartsWith("CeQuadrat ", StringComparison.OrdinalIgnoreCase))
            return null;

        uint pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(132, 4));
        uint primaryTypeLPathTableLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(140, 4));
        if (pathTableSize == 0 || primaryTypeLPathTableLba == 0 || pathTableSize > 1024 * 1024)
            return null;

        long linkTableLba = checked((long)primaryTypeLPathTableLba - 1);
        // The known WinOnCD layout places the private table directly after the
        // descriptor terminator and directly before the primary Type-L path table.
        // Requiring both relationships prevents an unused-sector guess.
        if (linkTableLba != terminatorLba + 1)
            return null;

        long relativeLinkTableLba = checked(linkTableLba - inspection.BaseLba);
        if (relativeLinkTableLba < 0 || relativeLinkTableLba >= inspection.SectorCount)
            return null;

        // The ordinary synthetic skeleton has an all-zero Mode 1 payload here on
        // the first Joliet preparation pass.  A later pass may encounter the exact
        // CeQuadrat bridge that *we just synthesized*.  Parse and retain that table
        // instead of disabling the CeQuadrat allocator on the rebuild pass.  An
        // original/donor-supplied bridge is even stronger evidence and is handled the
        // same way.
        stream.Position = relativeLinkTableLba * RawSectorSize;
        ReadExactly(stream, sector, cancellationToken);
        if (sector[15] != 1)
            return null;

        ReadOnlySpan<byte> existingLinkPayload = sector.AsSpan(16, CookedSectorSize);
        bool linkPayloadIsZero = true;
        for (int i = 0; i < existingLinkPayload.Length; i++)
        {
            if (existingLinkPayload[i] != 0x00)
            {
                linkPayloadIsZero = false;
                break;
            }
        }

        IReadOnlyDictionary<uint, uint>? existingJolietByPrimary = null;
        if (!linkPayloadIsZero)
        {
            ReadOnlySpan<byte> signature = "CeQuadrat Joliet directory link table"u8;
            if (!existingLinkPayload.StartsWith(signature) || existingLinkPayload.Length < 48)
                return null;

            uint pairCount = BinaryPrimitives.ReadUInt32LittleEndian(existingLinkPayload.Slice(44, 4));
            if (pairCount == 0 || 48L + pairCount * 8L > existingLinkPayload.Length)
                return null;

            var parsedPairs = new Dictionary<uint, uint>();
            int pairOffset = 48;
            for (uint i = 0; i < pairCount; i++)
            {
                uint jolietLba = BinaryPrimitives.ReadUInt32LittleEndian(existingLinkPayload.Slice(pairOffset, 4));
                uint primaryLba = BinaryPrimitives.ReadUInt32LittleEndian(existingLinkPayload.Slice(pairOffset + 4, 4));
                if (jolietLba == 0 || primaryLba == 0 || !parsedPairs.TryAdd(primaryLba, jolietLba))
                    return null;
                pairOffset += 8;
            }
            existingJolietByPrimary = parsedPairs;
        }

        long relativePathTableLba = checked((long)primaryTypeLPathTableLba - inspection.BaseLba);
        if (relativePathTableLba < 0 || relativePathTableLba >= inspection.SectorCount)
            return null;

        int tableBytes = checked((int)pathTableSize);
        byte[] pathTable = new byte[tableBytes];
        int copied = 0;
        long tableSector = relativePathTableLba;
        while (copied < tableBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tableSector >= inspection.SectorCount)
                return null;

            stream.Position = tableSector * RawSectorSize;
            ReadExactly(stream, sector, cancellationToken);
            int userOffset = sector[15] == 1 ? 16 : sector[15] == 2 ? 24 : -1;
            if (userOffset < 0)
                return null;

            int count = Math.Min(CookedSectorSize, tableBytes - copied);
            sector.AsSpan(userOffset, count).CopyTo(pathTable.AsSpan(copied, count));
            copied += count;
            tableSector++;
        }

        var extents = new List<uint>();
        int offset = 0;
        while (offset < pathTable.Length)
        {
            if (pathTable.Length - offset < 8)
                return null;

            int identifierLength = pathTable[offset];
            if (identifierLength <= 0)
                return null;

            int recordLength = 8 + identifierLength + (identifierLength & 1);
            if (offset + recordLength > pathTable.Length)
                return null;

            uint extent = BinaryPrimitives.ReadUInt32LittleEndian(pathTable.AsSpan(offset + 2, 4));
            if (extent == 0)
                return null;
            extents.Add(extent);
            offset += recordLength;
        }

        if (extents.Count == 0 || extents.Distinct().Count() != extents.Count)
            return null;

        if (existingJolietByPrimary is not null &&
            !existingJolietByPrimary.Keys.ToHashSet().SetEquals(extents))
            return null;

        return new CeQuadratLinkTableContext(linkTableLba, extents, existingJolietByPrimary);
    }

    private static IReadOnlyDictionary<string, PrimaryDirectoryMetadata> ReadPrimaryDirectoryMetadata(
        SkeletonInspectionResult inspection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PrimaryDirectoryMetadata>(StringComparer.OrdinalIgnoreCase);

        static byte[]? ReadSystemUse(ReadOnlySpan<byte> record, int identifierLength)
        {
            int start = 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);
            if (start >= record.Length)
                return null;
            return record.Slice(start).ToArray();
        }

        byte[] sector = new byte[RawSectorSize];
        byte[] payload = new byte[CookedSectorSize];
        byte[]? pvd = null;

        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess);

        long scanEnd = Math.Min(inspection.SectorCount, 64);
        for (long sectorIndex = 0; sectorIndex < scanEnd; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long lba = inspection.BaseLba + sectorIndex;
            if (!TryReadLogical2048(stream, inspection, lba, sector, payload, cancellationToken))
                continue;
            if (payload[0] == 1 && payload[1] == (byte)'C' && payload[2] == (byte)'D' &&
                payload[3] == (byte)'0' && payload[4] == (byte)'0' && payload[5] == (byte)'1' && payload[6] == 1)
            {
                pvd = (byte[])payload.Clone();
                break;
            }
        }

        if (pvd is null || pvd.Length < 190)
            return result;

        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
        uint rootLength = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
        byte rootFlags = pvd[181];
        byte[] rootRawTime = pvd.AsSpan(174, 7).ToArray();
        DateTimeOffset rootTime = TryReadIsoDirectoryTimestamp(rootRawTime, out DateTimeOffset parsedRootTime)
            ? parsedRootTime
            : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Extent/length/flags are independent structural evidence.  A malformed
        // ISO9660 recording timestamp (for example 1900-00-00 00:00:00) must not
        // discard the root geometry needed to reconstruct supplementary metadata.
        result["/"] = new PrimaryDirectoryMetadata(
            rootTime,
            rootFlags,
            RawRecordingTime: rootRawTime,
            PrimaryExtentLba: rootLba,
            PrimaryDataLength: rootLength,
            PrimaryPath: "/");

        var queue = new Queue<(string Path, long Lba, long Length)>();
        var seen = new HashSet<long>();
        if (rootLba > 0 && rootLength > 0)
            queue.Enqueue(("/", rootLba, rootLength));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string path, long dirLba, long dirLength) = queue.Dequeue();
            if (!seen.Add(dirLba))
                continue;

            long sectors = Math.Max(1, DivideRoundUp(dirLength, CookedSectorSize));
            byte[] bytes = new byte[checked((int)(sectors * CookedSectorSize))];
            bool complete = true;
            for (long i = 0; i < sectors; i++)
            {
                if (!TryReadLogical2048(stream, inspection, dirLba + i, sector, payload, cancellationToken))
                {
                    complete = false;
                    break;
                }
                Buffer.BlockCopy(payload, 0, bytes, checked((int)(i * CookedSectorSize)), CookedSectorSize);
            }
            if (!complete)
                continue;

            int limit = checked((int)Math.Min((long)bytes.Length, dirLength));
            int position = 0;
            while (position < limit)
            {
                int withinSector = position % CookedSectorSize;
                int recordLength = bytes[position];
                if (recordLength == 0)
                {
                    position += CookedSectorSize - withinSector;
                    continue;
                }
                if (recordLength < 34 || position + recordLength > limit)
                    break;

                ReadOnlySpan<byte> record = bytes.AsSpan(position, recordLength);
                byte flags = record[25];
                int identifierLength = record[32];
                if (identifierLength > 0 && 33 + identifierLength <= recordLength)
                {
                    ReadOnlySpan<byte> identifier = record.Slice(33, identifierLength);
                    bool dot = identifierLength == 1 && identifier[0] == 0;
                    bool dotdot = identifierLength == 1 && identifier[0] == 1;

                    if (dot)
                    {
                        // A directory has two distinct pieces of metadata: the entry visible
                        // in its parent directory and its internal "." record. They are not
                        // interchangeable. Project Eden, for example, gives DIRECTX80A a real
                        // parent-visible timestamp but an intentionally zeroed "." timestamp.
                        DateTimeOffset ownTime = TryReadIsoDirectoryTimestamp(record.Slice(18, 7), out DateTimeOffset parsedOwnTime)
                            ? parsedOwnTime
                            : result.TryGetValue(path, out PrimaryDirectoryMetadata? prior)
                                ? prior.RecordingTime
                                : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        byte[]? ownSystemUse = ReadSystemUse(record, identifierLength);
                        byte[] ownRawTime = record.Slice(18, 7).ToArray();

                        if (result.TryGetValue(path, out PrimaryDirectoryMetadata? existing))
                        {
                            result[path] = existing with
                            {
                                SelfRecordingTime = ownTime,
                                SelfSystemUse = ownSystemUse,
                                SelfRawRecordingTime = ownRawTime
                            };
                        }
                        else
                        {
                            // Root has no parent-visible entry, so its "." record is also the
                            // best available visible metadata.
                            result[path] = new PrimaryDirectoryMetadata(
                                ownTime, flags, ownSystemUse, ownRawTime,
                                ownTime, ownSystemUse, ownRawTime,
                                PrimaryExtentLba: dirLba,
                                PrimaryDataLength: dirLength,
                                PrimaryPath: path);
                        }
                    }
                    else if (dotdot)
                    {
                        DateTimeOffset parentLinkTime = TryReadIsoDirectoryTimestamp(record.Slice(18, 7), out DateTimeOffset parsedParentLinkTime)
                            ? parsedParentLinkTime
                            : result.TryGetValue(path, out PrimaryDirectoryMetadata? prior)
                                ? prior.RecordingTime
                                : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        byte[]? parentLinkSystemUse = ReadSystemUse(record, identifierLength);
                        byte[] parentLinkRawTime = record.Slice(18, 7).ToArray();

                        if (result.TryGetValue(path, out PrimaryDirectoryMetadata? existing))
                        {
                            result[path] = existing with
                            {
                                ParentLinkRecordingTime = parentLinkTime,
                                ParentLinkSystemUse = parentLinkSystemUse,
                                ParentLinkRawRecordingTime = parentLinkRawTime
                            };
                        }
                    }

                    if ((flags & (byte)IsoDirectoryRecordFlags.Directory) != 0 && !dot && !dotdot)
                    {
                        string name = Encoding.ASCII.GetString(identifier);
                        name = Regex.Replace(name, @";\d+$", string.Empty);
                        string childPath = path == "/" ? "/" + name : path.TrimEnd('/') + "/" + name;
                        DateTimeOffset childTime = TryReadIsoDirectoryTimestamp(record.Slice(18, 7), out DateTimeOffset parsedChildTime)
                            ? parsedChildTime
                            : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4));
                        uint childLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(10, 4));
                        result[childPath] = new PrimaryDirectoryMetadata(
                            childTime, flags, ReadSystemUse(record, identifierLength), record.Slice(18, 7).ToArray(),
                            PrimaryExtentLba: childLba, PrimaryDataLength: childLength, PrimaryPath: childPath,
                            PrimaryRecordOrder: position);
                        if (childLba > 0 && childLength > 0 && !seen.Contains(childLba))
                            queue.Enqueue((childPath, childLba, childLength));
                    }
                    else if (!dot && !dotdot)
                    {
                        string name = Encoding.ASCII.GetString(identifier);
                        name = Regex.Replace(name, @";\d+$", string.Empty);
                        string childPath = path == "/" ? "/" + name : path.TrimEnd('/') + "/" + name;
                        DateTimeOffset childTime = TryReadIsoDirectoryTimestamp(record.Slice(18, 7), out DateTimeOffset parsedChildTime)
                            ? parsedChildTime
                            : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        result[childPath] = new PrimaryDirectoryMetadata(
                            childTime, flags, ReadSystemUse(record, identifierLength), record.Slice(18, 7).ToArray(),
                            PrimaryPath: childPath, PrimaryRecordOrder: position);
                    }
                }

                position += recordLength;
            }
        }

        return result;
    }

    private static bool TryReadIsoDirectoryTimestamp(ReadOnlySpan<byte> source, out DateTimeOffset value)
    {
        value = default;
        if (source.Length < 7)
            return false;

        int year = 1900 + source[0];
        int month = source[1];
        int day = source[2];
        int hour = source[3];
        int minute = source[4];
        int second = source[5];
        int offsetQuarters = unchecked((sbyte)source[6]);
        if (year < 1900 || year > 2155 || month < 1 || month > 12 || day < 1 ||
            day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59 ||
            offsetQuarters < -48 || offsetQuarters > 52)
            return false;

        try
        {
            value = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetQuarters * 15));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static HashSet<long> DiscoverPrimaryIsoMetadataLbas(
        SkeletonInspectionResult inspection,
        CancellationToken cancellationToken)
    {
        var protectedLbas = new HashSet<long>();
        byte[] sector = new byte[RawSectorSize];
        byte[] payload = new byte[CookedSectorSize];
        long pvdLba = -1;
        byte[]? pvd = null;

        using (var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess))
        {
            long scanEnd = Math.Min(inspection.SectorCount, 64);
            for (long sectorIndex = 0; sectorIndex < scanEnd; sectorIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long lba = inspection.BaseLba + sectorIndex;
                if (!TryReadLogical2048(stream, inspection, lba, sector, payload, cancellationToken))
                    continue;

                if (payload[1] != (byte)'C' || payload[2] != (byte)'D' || payload[3] != (byte)'0' ||
                    payload[4] != (byte)'0' || payload[5] != (byte)'1' || payload[6] != 1)
                    continue;

                byte descriptorType = payload[0];
                // Volume descriptors belong to the ISO metadata region.  Preserve
                // the primary descriptor and terminator; the supplementary type-2
                // descriptor is intentionally allowed to change later.
                if (descriptorType != 2)
                    protectedLbas.Add(lba);

                if (descriptorType == 1 && pvd is null)
                {
                    pvdLba = lba;
                    pvd = (byte[])payload.Clone();
                }

                if (descriptorType == 255)
                    break;
            }

            if (pvd is null)
                return protectedLbas;

            uint pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(132, 4));
            uint typeLPathTable = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(140, 4));
            uint optionalTypeL = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(144, 4));
            uint typeMPathTable = BinaryPrimitives.ReadUInt32BigEndian(pvd.AsSpan(148, 4));
            uint optionalTypeM = BinaryPrimitives.ReadUInt32BigEndian(pvd.AsSpan(152, 4));
            long pathTableSectors = DivideRoundUp(pathTableSize, CookedSectorSize);

            foreach (uint start in new[] { typeLPathTable, optionalTypeL, typeMPathTable, optionalTypeM })
            {
                if (start == 0) continue;
                for (long i = 0; i < pathTableSectors; i++)
                    protectedLbas.Add(start + i);
            }

            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
            uint rootLength = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
            var queue = new Queue<(long Lba, long Length)>();
            var seenDirectories = new HashSet<long>();
            if (rootLba > 0 && rootLength > 0)
                queue.Enqueue((rootLba, rootLength));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (long dirLba, long dirLength) = queue.Dequeue();
                if (!seenDirectories.Add(dirLba))
                    continue;

                long sectorCount = Math.Max(1, DivideRoundUp(dirLength, CookedSectorSize));
                byte[] directoryBytes = new byte[checked((int)(sectorCount * CookedSectorSize))];
                bool complete = true;
                for (long i = 0; i < sectorCount; i++)
                {
                    long lba = dirLba + i;
                    protectedLbas.Add(lba);
                    if (!TryReadLogical2048(stream, inspection, lba, sector, payload, cancellationToken))
                    {
                        complete = false;
                        break;
                    }
                    Buffer.BlockCopy(payload, 0, directoryBytes, checked((int)(i * CookedSectorSize)), CookedSectorSize);
                }
                if (!complete)
                    continue;

                int limit = checked((int)Math.Min((long)directoryBytes.Length, dirLength));
                int position = 0;
                while (position < limit)
                {
                    int withinSector = position % CookedSectorSize;
                    int recordLength = directoryBytes[position];
                    if (recordLength == 0)
                    {
                        position += CookedSectorSize - withinSector;
                        continue;
                    }
                    if (recordLength < 34 || position + recordLength > limit)
                        break;

                    ReadOnlySpan<byte> record = directoryBytes.AsSpan(position, recordLength);
                    byte flags = record[25];
                    int identifierLength = record[32];
                    if ((flags & 0x02) != 0 && identifierLength > 0 && 33 + identifierLength <= recordLength)
                    {
                        ReadOnlySpan<byte> identifier = record.Slice(33, identifierLength);
                        bool dotEntry = identifierLength == 1 && (identifier[0] == 0 || identifier[0] == 1);
                        if (!dotEntry)
                        {
                            uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4));
                            uint childLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(10, 4));
                            if (childLba > 0 && childLength > 0 && !seenDirectories.Contains(childLba))
                                queue.Enqueue((childLba, childLength));
                        }
                    }

                    position += recordLength;
                }
            }
        }

        return protectedLbas;
    }

    private static bool TryReadLogical2048(
        FileStream stream,
        SkeletonInspectionResult inspection,
        long lba,
        byte[] sector,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        long sectorIndex = lba - inspection.BaseLba;
        if (sectorIndex < 0 || sectorIndex >= inspection.SectorCount)
            return false;

        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
        {
            stream.Position = sectorIndex * CookedSectorSize;
            ReadExactly(stream, payload, cancellationToken);
            return true;
        }

        if (inspection.ImageKind != SkeletonImageKind.Raw2352)
            return false;

        stream.Position = sectorIndex * RawSectorSize;
        ReadExactly(stream, sector, cancellationToken);
        byte mode = sector[15];
        int userOffset;
        if (mode == 1)
        {
            userOffset = 16;
        }
        else if (mode == 2 && (sector[18] & 0x20) == 0)
        {
            userOffset = 24;
        }
        else
        {
            return false;
        }

        Buffer.BlockCopy(sector, userOffset, payload, 0, CookedSectorSize);
        return true;
    }


    private static Dictionary<long, byte[]> SnapshotRawSectors(
        SkeletonInspectionResult inspection,
        IEnumerable<long> lbas,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, byte[]>();
        int physicalSectorSize = inspection.ImageKind == SkeletonImageKind.Cooked2048
            ? CookedSectorSize
            : RawSectorSize;

        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess);

        foreach (long lba in lbas.Distinct().OrderBy(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sectorIndex = lba - inspection.BaseLba;
            if (sectorIndex < 0 || sectorIndex >= inspection.SectorCount)
                continue;
            byte[] physical = new byte[physicalSectorSize];
            stream.Position = sectorIndex * physicalSectorSize;
            ReadExactly(stream, physical, cancellationToken);
            result[lba] = physical;
        }

        return result;
    }

    private static void RestoreRawSectors(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<long, byte[]> snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Count == 0)
            return;

        int physicalSectorSize = inspection.ImageKind == SkeletonImageKind.Cooked2048
            ? CookedSectorSize
            : RawSectorSize;

        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.RandomAccess);

        foreach (KeyValuePair<long, byte[]> pair in snapshot.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sectorIndex = pair.Key - inspection.BaseLba;
            if (sectorIndex < 0 || sectorIndex >= inspection.SectorCount)
                continue;
            if (pair.Value.Length != physicalSectorSize)
                throw new InvalidOperationException($"Saved metadata sector LBA {pair.Key:N0} has unexpected length {pair.Value.Length:N0}.");
            stream.Position = sectorIndex * physicalSectorSize;
            stream.Write(pair.Value, 0, pair.Value.Length);
        }
        stream.Flush(true);
    }

    private static void PatchRawMetadataSectors(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<long, byte[]> metadata,
        CancellationToken cancellationToken)
    {
        byte[] sector = new byte[RawSectorSize];
        using var stream = new FileStream(
            inspection.SkeletonPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            1024 * 1024,
            FileOptions.RandomAccess);

        foreach (KeyValuePair<long, byte[]> pair in metadata.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sectorIndex = pair.Key - inspection.BaseLba;
            if (sectorIndex < 0 || sectorIndex >= inspection.SectorCount)
                throw new InvalidOperationException($"Generated Joliet metadata LBA {pair.Key:N0} lies outside the synthetic skeleton.");
            if (pair.Value.Length != CookedSectorSize)
                throw new InvalidOperationException($"Generated Joliet metadata LBA {pair.Key:N0} is {pair.Value.Length:N0} bytes; expected {CookedSectorSize:N0}.");

            if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            {
                stream.Position = sectorIndex * CookedSectorSize;
                stream.Write(pair.Value, 0, pair.Value.Length);
                continue;
            }

            if (inspection.ImageKind != SkeletonImageKind.Raw2352)
                throw new InvalidOperationException($"Joliet metadata patching is unsupported for image kind {inspection.ImageKind}.");

            stream.Position = sectorIndex * RawSectorSize;
            ReadExactly(stream, sector, cancellationToken);
            byte logicalMode = (byte)(sector[15] & 0x03);
            if (logicalMode == 1)
            {
                SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                    sector,
                    pair.Value,
                    mode2Form2NoEdc: false,
                    dicLoggedMode2Form1EccError: false);
            }
            else if (logicalMode == 2)
            {
                if ((sector[18] & 0x20) != 0)
                    throw new InvalidOperationException($"Joliet metadata LBA {pair.Key:N0} is mapped as Mode 2 Form 2 and cannot hold a 2048-byte filesystem sector.");

                SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                    sector,
                    pair.Value,
                    mode2Form2NoEdc: false,
                    dicLoggedMode2Form1EccError: inspection.DicMode2Form1QFaultLbas?.Contains(pair.Key) == true);
            }
            else
            {
                throw new InvalidOperationException($"Joliet metadata LBA {pair.Key:N0} has unsupported raw sector mode 0x{sector[15]:x2}.");
            }

            SkeletonResurrectionService.ApplyDicFinalSectorRecipes(
                inspection,
                pair.Key,
                sector.AsSpan(0, sector.Length));

            stream.Position = sectorIndex * RawSectorSize;
            stream.Write(sector, 0, sector.Length);
        }

        stream.Flush(true);
    }


}
