using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    public async Task<SkeletonInspectionResult> InspectAsync(
        string skeletonPath,
        string hashPath,
        CancellationToken cancellationToken = default)
    {
        return await InspectAsync(
            skeletonPath,
            hashPath,
            preparationProgress: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SkeletonInspectionResult> InspectAsync(
        string skeletonPath,
        string hashPath,
        IProgress<SkeletonInputPreparationProgress>? preparationProgress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skeletonPath))
            throw new ArgumentException("Choose a skeleton file.", nameof(skeletonPath));
        if (string.IsNullOrWhiteSpace(hashPath))
            throw new ArgumentException("Choose the matching redumper .hash file.", nameof(hashPath));

        string skeleton = Path.GetFullPath(skeletonPath);
        string hash = Path.GetFullPath(hashPath);
        if (!File.Exists(skeleton))
            throw new FileNotFoundException("Skeleton file not found.", skeleton);
        if (!File.Exists(hash))
            throw new FileNotFoundException("Hash file not found.", hash);

        PreparedSkeletonInput prepared = await SkeletonInputMaterializer.PrepareAsync(
            skeleton,
            preparationProgress,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<HashManifestEntry> manifest = await ReadHashManifestAsync(hash, cancellationToken);
        if (manifest.Count == 0)
            throw new InvalidOperationException("The hash file does not contain any valid SHA1/path entries.");

        await using var reader = await SkeletonImageReader.OpenAsync(prepared.Path, cancellationToken);
        IsoTree isoTree = await ReadIsoTreeAsync(reader, cancellationToken);

        var byPath = new Dictionary<string, EntryBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (IsoFileExtent file in isoTree.Files)
        {
            string path = NormalizeIsoPath(file.Path);
            if (!byPath.TryGetValue(path, out EntryBuilder? current))
            {
                byPath[path] = new EntryBuilder(path, file.Lba, file.Length, SkeletonSpecialKind.None, true);
            }
            else
            {
                current.AddAlternateIsoRecord(file.Lba, file.Length);
                if (file.Length > current.DataLength)
                {
                    current.ExtentLba = file.Lba;
                    current.DataLength = file.Length;
                }
            }
        }

        IReadOnlyList<string> filesMissingFromHashManifest = FindFilesMissingFromHashManifest(
            byPath.Keys,
            manifest.Select(entry => entry.Path));

        var unmapped = new List<EntryBuilder>();
        foreach (HashManifestEntry item in manifest)
        {
            string manifestPath = NormalizeManifestPath(item.Path);

            // Prefer an exact ISO filename match first. Only treat a trailing .XA as
            // redumper's alternate Form2 hash when no real file with that name exists.
            if (byPath.TryGetValue(manifestPath, out EntryBuilder? target))
            {
                target.Sha1 = item.Sha1;
                continue;
            }

            if (manifestPath.EndsWith(".XA", StringComparison.OrdinalIgnoreCase))
            {
                string basePath = manifestPath[..^3];
                if (byPath.TryGetValue(basePath, out EntryBuilder? xaTarget))
                {
                    xaTarget.XaSha1 = item.Sha1;
                    continue;
                }
            }

            if (manifestPath.Equals("SYSTEM_AREA", StringComparison.OrdinalIgnoreCase))
            {
                byPath["SYSTEM_AREA"] = new EntryBuilder(
                    "SYSTEM_AREA",
                    (uint)reader.BaseLba,
                    SystemAreaSectors * CookedSectorSize,
                    SkeletonSpecialKind.SystemArea,
                    true)
                {
                    Sha1 = item.Sha1
                };
                continue;
            }

            if (TryParseGapLba(manifestPath, out uint gapLba))
            {
                bool gapXa = manifestPath.EndsWith(".XA", StringComparison.OrdinalIgnoreCase);
                string gapKey = gapXa ? manifestPath[..^3] : manifestPath;
                if (!byPath.TryGetValue(gapKey, out EntryBuilder? gapTarget))
                {
                    long gapLength = isoTree.GetGapPayloadLength(gapLba, CookedSectorSize);
                    gapTarget = new EntryBuilder(gapKey, gapLba, gapLength, SkeletonSpecialKind.Gap, true);
                    byPath[gapKey] = gapTarget;
                }

                if (gapXa)
                    gapTarget.XaSha1 = item.Sha1;
                else
                    gapTarget.Sha1 = item.Sha1;
                continue;
            }

            var unknown = new EntryBuilder(manifestPath, 0, 0, SkeletonSpecialKind.UnmappedHashEntry, false)
            {
                Sha1 = item.Sha1
            };
            unmapped.Add(unknown);
        }

        var entries = byPath.Values
            .Concat(unmapped)
            .Select(b => b.ToEntry())
            .OrderBy(e => e.SpecialKind == SkeletonSpecialKind.None ? 0 : 1)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        NeroSystemAreaRecoveryInfo? neroSystemAreaRecovery = null;
        var neroRequirements = isoTree.NeroProjectRequirements.ToList();
        try
        {
            byte[] sector15 = await reader.ReadForm1SectorAsync(
                reader.BaseLba + SystemAreaSectors - 1,
                cancellationToken).ConfigureAwait(false);
            if (NeroNriDetector.TryParseSystemAreaRecord(
                    sector15,
                    out NeroNriDetector.SystemAreaRecord? systemAreaRecord) &&
                systemAreaRecord is not null &&
                !neroRequirements.Any(requirement =>
                    requirement.FileName.Equals(systemAreaRecord.FileName, StringComparison.OrdinalIgnoreCase) &&
                    requirement.ExtentLba == systemAreaRecord.ExtentLba &&
                    requirement.DataLength == systemAreaRecord.DataLength))
            {
                neroRequirements.Add(new NeroNriPayloadRequirement(
                    systemAreaRecord.FileName,
                    systemAreaRecord.ExtentLba,
                    systemAreaRecord.DataLength,
                    "valid Nero sector-15 record"));
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            // An unreadable system-area sector is not Nero evidence by itself.
        }

        var neroNriWarnings = new List<string>();
        foreach (NeroNriPayloadRequirement requirement in neroRequirements
                     .DistinctBy(item => (item.FileName.ToUpperInvariant(), item.ExtentLba, item.DataLength)))
        {
            bool payloadPresent = isoTree.NeroProjects.Any(project =>
                project.FileName.Equals(requirement.FileName, StringComparison.OrdinalIgnoreCase) &&
                project.Lba == requirement.ExtentLba &&
                project.DataLength == requirement.DataLength);
            if (!payloadPresent)
                payloadPresent = await HasNeroPayloadSignatureAsync(reader, requirement, cancellationToken).ConfigureAwait(false);

            if (!payloadPresent)
            {
                neroNriWarnings.Add(
                    $"{requirement.Evidence} requires Nero project '{requirement.FileName}' at LBA {requirement.ExtentLba:N0} " +
                    $"({requirement.DataLength:N0} bytes), but the skeleton does not contain a valid length-prefixed NeroISO payload there. " +
                    "The NRI payload cannot be generated; supply an exact source image or NRI file.");
            }
        }

        SkeletonContentEntry? systemArea = entries.FirstOrDefault(entry => entry.SpecialKind == SkeletonSpecialKind.SystemArea);
        if (systemArea?.Sha1 is { Length: 40 } expectedSystemAreaSha1 &&
            !expectedSystemAreaSha1.Equals(ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) &&
            isoTree.NeroProject is { } neroProject &&
            await IsLogicalSystemAreaZeroAsync(reader, cancellationToken))
        {
            neroSystemAreaRecovery = new NeroSystemAreaRecoveryInfo(
                neroProject.FileName,
                neroProject.Lba,
                neroProject.DataLength,
                neroProject.Signature,
                expectedSystemAreaSha1.ToLowerInvariant());
        }

        return new SkeletonInspectionResult(
            skeleton,
            hash,
            reader.Kind,
            reader.SectorSize,
            reader.BaseLba,
            reader.SectorCount,
            entries,
            isoTree.VolumeIdentifier,
            manifest.Count,
            unmapped.Count)
        {
            MaterializedSkeletonPath = prepared.WasMaterialized ? prepared.Path : null,
            SkeletonInputFormat = prepared.Format,
            FilesMissingFromHashManifest = filesMissingFromHashManifest,
            NeroSystemAreaRecovery = neroSystemAreaRecovery,
            NeroNriWarnings = neroNriWarnings
        };
    }

    private static async Task<bool> HasNeroPayloadSignatureAsync(
        SkeletonImageReader image,
        NeroNriPayloadRequirement requirement,
        CancellationToken cancellationToken)
    {
        if (requirement.DataLength < 8 ||
            requirement.ExtentLba < image.BaseLba ||
            requirement.ExtentLba >= image.BaseLba + image.SectorCount)
        {
            return false;
        }

        try
        {
            byte[] header = await image.ReadForm1BytesAsync(
                requirement.ExtentLba,
                Math.Min(requirement.DataLength, 256u),
                cancellationToken).ConfigureAwait(false);
            return NeroNriDetector.TryReadPayloadSignature(header, out _);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> IsLogicalSystemAreaZeroAsync(
        SkeletonImageReader image,
        CancellationToken cancellationToken)
    {
        for (int sector = 0; sector < SystemAreaSectors; sector++)
        {
            byte[] payload = await image.ReadForm1SectorAsync(image.BaseLba + sector, cancellationToken);
            if (payload.Any(value => value != 0))
                return false;
        }

        return true;
    }

    internal static IReadOnlyList<string> FindFilesMissingFromHashManifest(
        IEnumerable<string> skeletonFilePaths,
        IEnumerable<string> manifestPaths)
    {
        var exactManifestPaths = manifestPaths
            .Select(NormalizeManifestPath)
            .Where(path => path.StartsWith('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return skeletonFilePaths
            .Select(NormalizeIsoPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !exactManifestPaths.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
