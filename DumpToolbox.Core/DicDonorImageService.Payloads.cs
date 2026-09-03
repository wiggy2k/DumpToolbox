using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{

    private sealed record DonorPayloadSelection(DicDonorFile File, string Method);

    internal static string AddDonorJolietProvenance(string method)
    {
        if (method.Contains("Joliet", StringComparison.OrdinalIgnoreCase))
            return method;

        return string.IsNullOrWhiteSpace(method)
            ? "Donor Joliet pathname -> DIC primary ISO9660 record + exact path+size"
            : method + " + mapped Joliet pathname";
    }

    private static DonorPayloadSelection? FindStrongDonorPayloadMatch(
        SkeletonContentEntry entry,
        IReadOnlyList<SkeletonContentEntry> required,
        IReadOnlyDictionary<long, List<DicDonorFile>> bySize,
        IReadOnlyDictionary<DicDonorFile, DicDonorFile> jolietByPrimary,
        JolietNamingProfile? namingProfile)
    {
        if (!bySize.TryGetValue(entry.DataLength, out List<DicDonorFile>? sameSize))
            return null;

        string[] aliases = GetEntryAliases(entry);
        DicDonorFile[] exact = sameSize
            .Where(candidate => candidate.FileFlags == entry.IsoFileFlags)
            .Where(candidate => aliases.Any(alias =>
            {
                string expectedPath = NormalizePath(entry.IsoOriginalPath ?? alias);
                string candidatePath = NormalizePath(candidate.Path);
                return expectedPath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(expectedPath).Equals(Path.GetFileName(candidatePath), StringComparison.OrdinalIgnoreCase);
            }))
            .ToArray();

        if (exact.Length == 1)
            return new DonorPayloadSelection(exact[0], "Donor ISO9660 exact relative path+filename+size");
        if (exact.Length != 0)
            return null;

        DicDonorFile[] projected = sameSize
            .Where(candidate => candidate.FileFlags == entry.IsoFileFlags)
            .Where(candidate => jolietByPrimary.TryGetValue(candidate, out DicDonorFile? donorJoliet) &&
                aliases.Any(alias => SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(
                    NormalizePath(donorJoliet.Path),
                    NormalizePath(entry.IsoOriginalPath ?? alias),
                    namingProfile)))
            .ToArray();

        if (projected.Length > 1 && entry.RecordingTime is DateTimeOffset expectedTime)
        {
            DicDonorFile[] timestampMatches = projected
                .Where(candidate => candidate.RecordingTime is DateTimeOffset candidateTime && candidateTime.Equals(expectedTime))
                .ToArray();
            if (timestampMatches.Length == 1)
                projected = timestampMatches;
        }

        if (projected.Length != 1 || !jolietByPrimary.TryGetValue(projected[0], out DicDonorFile? projectedJoliet))
            return null;

        string donorJolietPath = NormalizePath(projectedJoliet.Path);
        int compatibleTargets = required.Count(other =>
            other.DataLength == entry.DataLength &&
            other.IsoFileFlags == entry.IsoFileFlags &&
            GetEntryAliases(other).Any(alias =>
                SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(
                    donorJolietPath,
                    NormalizePath(other.IsoOriginalPath ?? alias),
                    namingProfile)));

        return compatibleTargets == 1
            ? new DonorPayloadSelection(projected[0], "Donor Joliet pathname -> DIC primary ISO9660 projection + exact size")
            : null;
    }

    private static bool TryGetTargetTildeFamily(
        SkeletonContentEntry entry,
        out string parent,
        out string familyKey,
        out int aliasIndex,
        out string extension)
    {
        foreach (string alias in GetEntryAliases(entry))
        {
            string normalized = NormalizePath(entry.IsoOriginalPath ?? alias);
            string leaf = Regex.Replace(Path.GetFileName(normalized).Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
            int dot = leaf.LastIndexOf('.');
            string stem = dot > 0 ? leaf[..dot] : leaf;
            extension = dot > 0 ? leaf[(dot + 1)..].ToUpperInvariant() : string.Empty;
            Match match = Regex.Match(stem, @"^(?<prefix>.*)~(?<index>[1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out aliasIndex))
                continue;

            string stablePrefix = new(match.Groups["prefix"].Value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (stablePrefix.Length > 5)
                stablePrefix = stablePrefix[..5];
            if (stablePrefix.Length < 2)
                continue;

            int slash = normalized.LastIndexOf('/');
            parent = slash >= 0 ? normalized[..slash].ToUpperInvariant() : string.Empty;
            familyKey = stablePrefix;
            return true;
        }

        parent = string.Empty;
        familyKey = string.Empty;
        aliasIndex = 0;
        extension = string.Empty;
        return false;
    }

    private static bool JolietLeafMatchesAliasFamily(string jolietPath, string familyKey, string extension)
    {
        string leaf = Regex.Replace(Path.GetFileName(NormalizePath(jolietPath)).Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        int dot = leaf.LastIndexOf('.');
        string stem = dot > 0 ? leaf[..dot] : leaf;
        string sourceExtension = dot > 0 ? leaf[(dot + 1)..] : string.Empty;
        if (!sourceExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            return false;

        string normalizedStem = new(stem.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalizedStem.StartsWith(familyKey, StringComparison.OrdinalIgnoreCase);
    }

    private static DonorPayloadSelection? FindOrderedAliasFamilyMatch(
        SkeletonContentEntry entry,
        IReadOnlyList<SkeletonContentEntry> required,
        IReadOnlyList<DicDonorFile> donorPrimaryFiles,
        IReadOnlyDictionary<DicDonorFile, DicDonorFile> jolietByPrimary,
        IReadOnlyDictionary<string, DonorPayloadSelection> strongSelections)
    {
        if (!TryGetTargetTildeFamily(entry, out string targetParent, out string familyKey, out _, out string extension))
            return null;

        // A short-name collision family must not be split by recording timestamp.
        // Real mastering runs can assign adjacent family members timestamps a second
        // apart (for example UBI_LO~1.BMP / UBI_LO~2.BMP), while the donor still
        // preserves their definitive directory-record order. Family identity is
        // therefore parent + projected stem + extension + exact size + flags.
        SkeletonContentEntry[] targetFamily = required
            .Where(target => target.DataLength == entry.DataLength && target.IsoFileFlags == entry.IsoFileFlags)
            .Select(target => (Target: target, Ok: TryGetTargetTildeFamily(target, out string parent, out string key, out int index, out string ext), Parent: parent, Key: key, Index: index, Ext: ext))
            .Where(x => x.Ok && x.Parent.Equals(targetParent, StringComparison.OrdinalIgnoreCase) && x.Key.Equals(familyKey, StringComparison.OrdinalIgnoreCase) && x.Ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Index)
            .ThenBy(x => x.Target.ExtentLba)
            .Select(x => x.Target)
            .ToArray();

        if (targetFamily.Length < 2)
            return null;

        var sourceGroups = donorPrimaryFiles
            .Where(candidate => candidate.FileFlags == entry.IsoFileFlags && candidate.DataLength == entry.DataLength)
            .Where(candidate => jolietByPrimary.TryGetValue(candidate, out DicDonorFile? joliet) && JolietLeafMatchesAliasFamily(joliet.Path, familyKey, extension))
            .GroupBy(candidate =>
            {
                DicDonorFile joliet = jolietByPrimary[candidate];
                string path = NormalizePath(joliet.Path);
                int slash = path.LastIndexOf('/');
                return slash >= 0 ? path[..slash].ToUpperInvariant() : string.Empty;
            }, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == targetFamily.Length)
            .ToArray();

        if (sourceGroups.Length != 1)
            return null;

        DicDonorFile[] sourceFamily = sourceGroups[0]
            .OrderBy(source => source.DirectoryExtentLba)
            .ThenBy(source => source.DirectoryRecordIndex < 0 ? int.MaxValue : source.DirectoryRecordIndex)
            .ThenBy(source => source.DirectoryRecordOffset < 0 ? int.MaxValue : source.DirectoryRecordOffset)
            .ThenBy(source => source.ExtentLba)
            .ToArray();

        // Any member already resolved by a stronger rule must occupy the same ordinal
        // position. This lets a proven member constrain the remaining ambiguous family.
        // Build an explicit assignment table so recording timestamps can provide an
        // additional discriminator before we fall back to directory-record order.
        var assignedSourceByTarget = new int?[targetFamily.Length];
        var sourceAlreadyUsed = new bool[sourceFamily.Length];
        for (int i = 0; i < targetFamily.Length; i++)
        {
            if (!strongSelections.TryGetValue(targetFamily[i].Path, out DonorPayloadSelection? anchor))
                continue;

            int sourceIndex = Array.FindIndex(sourceFamily, source => ReferenceEquals(source, anchor.File) || source == anchor.File);
            if (sourceIndex < 0 || sourceIndex != i || sourceAlreadyUsed[sourceIndex])
                return null;

            assignedSourceByTarget[i] = sourceIndex;
            sourceAlreadyUsed[sourceIndex] = true;
        }

        // Where both sides preserve ISO9660 recording times, use the exact timestamp
        // inside the proven alias family. If several family members share one timestamp,
        // retain filesystem order only within that timestamp bucket. This handles pairs
        // such as UBI_LO~1/~2 where the members are one second apart without letting the
        // timestamp split the family itself.
        bool allRemainingSourcesHaveTimestamps = sourceFamily
            .Select((source, index) => (source, index))
            .Where(x => !sourceAlreadyUsed[x.index])
            .All(x => x.source.RecordingTime is not null);

        DateTimeOffset[] targetTimestamps = targetFamily
            .Select(target => target.RecordingTime)
            .Where(time => time is not null)
            .Select(time => time!.Value)
            .Distinct()
            .ToArray();

        foreach (DateTimeOffset timestamp in targetTimestamps)
        {
            int[] targetIndexes = targetFamily
                .Select((target, index) => (target, index))
                .Where(x => assignedSourceByTarget[x.index] is null && x.target.RecordingTime is DateTimeOffset targetTime && targetTime.Equals(timestamp))
                .Select(x => x.index)
                .ToArray();

            if (targetIndexes.Length == 0)
                continue;

            int[] sourceIndexes = sourceFamily
                .Select((source, index) => (source, index))
                .Where(x => !sourceAlreadyUsed[x.index] && x.source.RecordingTime is DateTimeOffset sourceTime && sourceTime.Equals(timestamp))
                .Select(x => x.index)
                .ToArray();

            if (sourceIndexes.Length != targetIndexes.Length)
            {
                if (allRemainingSourcesHaveTimestamps)
                    return null;
                continue;
            }

            for (int i = 0; i < targetIndexes.Length; i++)
            {
                assignedSourceByTarget[targetIndexes[i]] = sourceIndexes[i];
                sourceAlreadyUsed[sourceIndexes[i]] = true;
            }
        }

        // Timestamp evidence may be unavailable for some members. Pair only those
        // remaining members by the already-proven family order.
        int[] remainingTargets = Enumerable.Range(0, targetFamily.Length)
            .Where(index => assignedSourceByTarget[index] is null)
            .ToArray();
        int[] remainingSources = Enumerable.Range(0, sourceFamily.Length)
            .Where(index => !sourceAlreadyUsed[index])
            .ToArray();
        if (remainingTargets.Length != remainingSources.Length)
            return null;

        for (int i = 0; i < remainingTargets.Length; i++)
            assignedSourceByTarget[remainingTargets[i]] = remainingSources[i];

        int targetPosition = Array.FindIndex(targetFamily, target => target.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        if (targetPosition < 0 || assignedSourceByTarget[targetPosition] is not int selectedSourceIndex)
            return null;

        bool matchedByTimestamp = entry.RecordingTime is DateTimeOffset entryTime &&
            sourceFamily[selectedSourceIndex].RecordingTime is DateTimeOffset sourceTime &&
            sourceTime.Equals(entryTime);

        return new DonorPayloadSelection(
            sourceFamily[selectedSourceIndex],
            matchedByTimestamp
                ? "Donor proven alias-family exact timestamp+size + filesystem order"
                : "Donor proven alias-family filesystem order + exact size");
    }

    private static string[] GetEntryAliases(SkeletonContentEntry entry)
        => (entry.PathAliases ?? Array.Empty<string>())
            .Append(entry.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private static string BuildDonorCacheId(string donorPath)
    {
        var info = new FileInfo(donorPath);
        string identity = Path.GetFullPath(donorPath) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string BuildCachedSourcePath(string cacheDirectory, SkeletonContentEntry entry, DicDonorFile donorFile)
    {
        string displayName = Path.GetFileName(donorFile.Path);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = Path.GetFileName(entry.Path);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "payload.bin";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            displayName = displayName.Replace(invalid, '_');

        string key = entry.Path + "|" + entry.ExtentLba + "|" + entry.DataLength;
        string shortHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 6)).ToLowerInvariant();
        return Path.Combine(cacheDirectory, shortHash + "_" + displayName);
    }

    private static async Task ExtractFileAsync(
        DonorImageReader donor,
        DicDonorFile file,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string temp = destinationPath + ".partial";
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            await using (var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                IReadOnlyList<DicDonorExtent> extents = file.Extents is { Count: > 0 }
                    ? file.Extents
                    : new[]
                    {
                        new DicDonorExtent(
                            file.ExtentLba,
                            file.DataLength,
                            file.ExtendedAttributeRecordLength,
                            file.FileUnitSize,
                            file.InterleaveGapSize)
                    };

                foreach (DicDonorExtent extent in extents)
                {
                    long remaining = extent.DataLength;
                    bool interleaved = extent.FileUnitSize != 0 || extent.InterleaveGapSize != 0;
                    int unitBlocks = interleaved ? Math.Max(1, extent.FileUnitSize) : int.MaxValue;
                    int gapBlocks = interleaved ? Math.Max(0, extent.InterleaveGapSize) : 0;
                    long lba = interleaved && extent.ExtendedAttributeRecordLength > 0
                        ? checked((long)extent.ExtentLba + unitBlocks + gapBlocks)
                        : checked((long)extent.ExtentLba + Math.Max(0, extent.ExtendedAttributeRecordLength));

                    while (remaining > 0)
                    {
                        long unitStartLba = lba;
                        long unitRemaining = interleaved
                            ? Math.Min(remaining, checked((long)unitBlocks * CookedSectorSize))
                            : remaining;

                        while (unitRemaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            byte[] payload = await donor.ReadPayloadSectorAsync(lba++, cancellationToken).ConfigureAwait(false);
                            int count = (int)Math.Min((long)payload.Length, unitRemaining);
                            await output.WriteAsync(payload.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                            unitRemaining -= count;
                            remaining -= count;
                        }

                        if (remaining > 0 && interleaved)
                            lba = checked(unitStartLba + unitBlocks + gapBlocks);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, destinationPath, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task<int> ApplyRequiredPayloadsAsync(
        SkeletonInspectionResult inspection,
        DonorImageReader donor,
        IReadOnlyList<DicDonorFile> donorFiles,
        IReadOnlyList<SkeletonDonorRequirement> requirements,
        IProgress<DicDonorProgress>? progress,
        CancellationToken cancellationToken,
        bool strict,
        List<string> warnings)
    {
        if (!File.Exists(inspection.SkeletonPath))
            throw new FileNotFoundException("DIC synthetic skeleton not found.", inspection.SkeletonPath);

        await using var target = new FileStream(
            inspection.SkeletonPath,
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
        for (int requirementIndex = 0; requirementIndex < requirements.Count; requirementIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkeletonDonorRequirement requirement = requirements[requirementIndex];

            try
            {
                DicDonorFile? source = null;
                if (requirement.RequireRecordMatch)
                {
                    DicDonorFile[] exact = donorFiles
                        .Where(file =>
                                       file.ExtentLba == requirement.ExtentLba &&
                                       file.DataLength == requirement.DataLength &&
                                       file.FileFlags == requirement.FileFlags &&
                                       file.ExtendedAttributeRecordLength == requirement.ExtendedAttributeRecordLength &&
                                       file.FileUnitSize == requirement.FileUnitSize &&
                                       file.InterleaveGapSize == requirement.InterleaveGapSize &&
                                       NormalizePath(file.Path).Equals(NormalizePath(requirement.Path), StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (exact.Length != 1)
                    {
                        throw new InvalidOperationException(
                            $"The same-disc donor does not contain exactly one matching ISO 9660 record for '{requirement.Path}' " +
                            $"at LBA {requirement.ExtentLba:N0} ({requirement.DataLength:N0} bytes, flags 0x{requirement.FileFlags:X2}; {requirement.Reason}). " +
                            $"Found {exact.Length:N0} matching record(s).");
                    }

                    source = exact[0];
                }

                if (requirement.RequiresRawDonor && donor.SectorSize != RawSectorSize)
                {
                    throw new InvalidOperationException(
                        $"Exactness region for '{requirement.Path}' requires a 2352-byte raw BIN donor because its framing/protection/audio bytes cannot be reconstructed from a cooked 2048-byte ISO donor.");
                }

                if (requirement.ContainsMode2Form2 && donor.SectorSize == CookedSectorSize)
                {
                    throw new InvalidOperationException(
                        $"Required donor region for '{requirement.Path}' includes Mode 2 Form 2 sectors. A 2048-byte ISO donor cannot preserve the full 2324-byte Form 2 payload; use a 2352-byte raw BIN donor.");
                }

                long targetStart = requirement.ExtentLba;
                long donorStart = source?.ExtentLba ?? requirement.ExtentLba;
                long targetStartIndex = targetStart - inspection.BaseLba;
                if (targetStartIndex < 0 || requirement.PhysicalSectorCount < 0 ||
                    targetStartIndex + requirement.PhysicalSectorCount > target.Length / targetSectorSize)
                {
                    throw new InvalidOperationException(
                        $"Required-donor target LBA range {targetStart:N0}-{targetStart + requirement.PhysicalSectorCount - 1:N0} is outside the DIC skeleton.");
                }
                if (donorStart < 0 || donorStart + requirement.PhysicalSectorCount > donor.SectorCount)
                {
                    throw new InvalidOperationException(
                        $"Donor image does not contain the complete LBA range {donorStart:N0}-{donorStart + requirement.PhysicalSectorCount - 1:N0} needed for {requirement.Reason}.");
                }

                for (long sectorOffset = 0; sectorOffset < requirement.PhysicalSectorCount; sectorOffset++)
                {
                    long targetLba = targetStart + sectorOffset;
                    long donorLba = donorStart + sectorOffset;

                    if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
                    {
                        byte[] cookedPayload = await donor.ReadPayloadSectorAsync(donorLba, cancellationToken).ConfigureAwait(false);
                        if (cookedPayload.Length != CookedSectorSize)
                        {
                            throw new InvalidOperationException(
                                $"Donor LBA {donorLba:N0} provides {cookedPayload.Length:N0} payload bytes; a cooked target requires exactly {CookedSectorSize:N0} bytes.");
                        }

                        long targetIndex = targetLba - inspection.BaseLba;
                        if (targetIndex < 0 || targetIndex >= inspection.SectorCount)
                            throw new InvalidOperationException($"Required-donor target LBA {targetLba:N0} is outside the cooked DIC skeleton.");

                        target.Position = targetIndex * CookedSectorSize;
                        await target.WriteAsync(cookedPayload.AsMemory(0, cookedPayload.Length), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (donor.SectorSize == RawSectorSize)
                    {
                        byte[] donorRaw = await donor.ReadRawSectorAsync(donorLba, cancellationToken).ConfigureAwait(false);

                        // A raw same-disc donor is copied verbatim so unproven bytes such as
                        // tail slack and post-volume sectors are preserved. However, that donor
                        // may have a different physical ECC representation from the DIC dump.
                        // Reapply any mastering fault explicitly proven by the DIC EccEdc error
                        // list before the raw sector is committed. Without this, the donor copy
                        // can overwrite the fault already present in the synthetic skeleton.
                        SkeletonResurrectionService.ApplyDicLoggedFramingOverrides(
                            inspection,
                            targetLba,
                            donorRaw.AsSpan(0, donorRaw.Length));
                        SkeletonResurrectionService.ApplyDicFinalSectorRecipes(
                            inspection,
                            targetLba,
                            donorRaw.AsSpan(0, donorRaw.Length));

                        target.Position = (targetLba - inspection.BaseLba) * RawSectorSize;
                        await target.WriteAsync(donorRaw.AsMemory(0, donorRaw.Length), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    byte[] payload = await donor.ReadPayloadSectorAsync(donorLba, cancellationToken).ConfigureAwait(false);

                    target.Position = (targetLba - inspection.BaseLba) * RawSectorSize;
                    await ReadExactlyAsync(target, raw, cancellationToken).ConfigureAwait(false);
                    byte logicalMode = (byte)(raw[15] & 0x03);
                    if (logicalMode == 1)
                    {
                        if (payload.Length != CookedSectorSize)
                            throw new InvalidOperationException($"Donor LBA {donorLba:N0} does not provide the 2048-byte payload required by target Mode 1 LBA {targetLba:N0}.");
                        SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                            raw,
                            payload,
                            mode2Form2NoEdc: false,
                            dicLoggedMode2Form1EccError: false);
                    }
                    else if (logicalMode == 2)
                    {
                        bool form2 = (raw[18] & 0x20) != 0;
                        int expectedPayload = form2 ? 2324 : CookedSectorSize;
                        if (payload.Length != expectedPayload)
                            throw new InvalidOperationException($"Donor LBA {donorLba:N0} does not provide the {expectedPayload:N0}-byte payload required by target Mode 2 LBA {targetLba:N0}.");

                        SkeletonResurrectionService.ReplacePayloadPreservingFraming(
                            raw,
                            payload,
                            mode2Form2NoEdc: form2 && inspection.NoEdcLbas?.Contains(targetLba) == true,
                            dicLoggedMode2Form1EccError: !form2 && inspection.DicMode2Form1QFaultLbas?.Contains(targetLba) == true);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported target sector mode at required-donor LBA {targetLba:N0}; a cooked donor cannot reproduce Mode 0/audio/unknown raw sectors.");
                    }

                    SkeletonResurrectionService.ApplyDicFinalSectorRecipes(
                        inspection,
                        targetLba,
                        raw.AsSpan(0, raw.Length));

                    target.Position = (targetLba - inspection.BaseLba) * RawSectorSize;
                    await target.WriteAsync(raw.AsMemory(0, raw.Length), cancellationToken).ConfigureAwait(false);
                }

                applied++;
                progress?.Report(new DicDonorProgress(
                    requirementIndex + 1,
                    requirements.Count,
                    strict
                        ? $"Applying mandatory ISO9660 payloads — {requirementIndex + 1:N0}/{requirements.Count:N0}"
                        : $"Applying optional exactness sectors — {requirementIndex + 1:N0}/{requirements.Count:N0}"));
            }
            catch (Exception ex) when (!strict && ex is not OperationCanceledException)
            {
                warnings.Add(
                    $"Optional exactness region was not fully restored from donor ({requirement.Reason}, LBA {requirement.ExtentLba:N0}, {requirement.PhysicalSectorCount:N0} sector(s)): {ex.Message}");
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

}
