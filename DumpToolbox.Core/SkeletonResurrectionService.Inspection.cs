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

        IReadOnlyList<HashManifestEntry> manifest = await ReadHashManifestAsync(hash, cancellationToken);
        if (manifest.Count == 0)
            throw new InvalidOperationException("The hash file does not contain any valid SHA1/path entries.");

        await using var reader = await SkeletonImageReader.OpenAsync(skeleton, cancellationToken);
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
            unmapped.Count);
    }
}
