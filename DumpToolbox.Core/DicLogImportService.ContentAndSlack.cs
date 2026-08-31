using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private static SkeletonDonorRequirement BuildFullPayloadDonorRequirement(
        DicFileRecord file,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount,
        string reason)
    {
        long physicalSectors = CountDonorCoverageSectors(file, sectorLayouts, defaultMode, sectorCount);
        bool containsForm2 = RegionContainsForm2(file.ExtentLba, physicalSectors, sectorLayouts, defaultMode, sectorCount);

        return new SkeletonDonorRequirement(
            file.Path,
            checked((uint)Math.Max(0, file.ExtentLba)),
            file.DataLength,
            physicalSectors,
            containsForm2,
            (byte)file.Flags,
            file.ExtendedAttributeRecordLength,
            file.FileUnitSize,
            file.InterleaveGapSize,
            reason,
            RequireRecordMatch: (file.Flags & IsoDirectoryRecordFlags.Directory) == 0);
    }

    private static SkeletonDonorRequirement BuildXarDonorRequirement(
        DicFileRecord file,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount)
    {
        long xarSectors = Math.Max(0, file.ExtendedAttributeRecordLength);
        bool containsForm2 = RegionContainsForm2(file.ExtentLba, xarSectors, sectorLayouts, defaultMode, sectorCount);
        string reason = (file.Flags & IsoDirectoryRecordFlags.Directory) != 0
            ? $"directory Extended Attribute Record ({xarSectors} block(s))"
            : $"file Extended Attribute Record ({xarSectors} block(s))";

        return new SkeletonDonorRequirement(
            file.Path,
            checked((uint)Math.Max(0, file.ExtentLba)),
            file.DataLength,
            xarSectors,
            containsForm2,
            (byte)file.Flags,
            file.ExtendedAttributeRecordLength,
            file.FileUnitSize,
            file.InterleaveGapSize,
            reason,
            RequireRecordMatch: (file.Flags & IsoDirectoryRecordFlags.Directory) == 0);
    }

    private static bool RegionContainsForm2(
        long startLba,
        long sectorLength,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount)
    {
        for (long offset = 0; offset < sectorLength; offset++)
        {
            long lba = startLba + offset;
            if (lba < 0 || lba >= sectorCount)
                break;
            DicSectorLayout layout = GetLayout(sectorLayouts, lba, defaultMode);
            if (layout.Mode == 2 && layout.Form == 2)
                return true;
        }
        return false;
    }

    private static long CountDonorCoverageSectors(
        DicFileRecord file,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount)
    {
        long xarBlocks = Math.Max(0, file.ExtendedAttributeRecordLength);
        long dataStartLba = file.ExtentLba + xarBlocks;

        if (file.FileUnitSize != 0 || file.InterleaveGapSize != 0)
        {
            long logicalBlocks = DivideRoundUp(Math.Max(0, file.DataLength), CookedSectorSize);
            int unit = Math.Max(1, file.FileUnitSize);
            long units = logicalBlocks == 0 ? 0 : DivideRoundUp(logicalBlocks, unit);
            long gapCount = units <= 0 ? 0 : (file.ExtendedAttributeRecordLength > 0 ? units : Math.Max(0, units - 1));
            long gaps = checked(gapCount * Math.Max(0, file.InterleaveGapSize));
            return checked(xarBlocks + logicalBlocks + gaps);
        }

        long dataSectors = file.DataLength == 0
            ? 0
            : CountPhysicalSectors(dataStartLba, file.DataLength, sectorLayouts, defaultMode, sectorCount);
        return checked(xarBlocks + dataSectors);
    }

    private static IReadOnlyList<SkeletonExtentSegment> BuildDataSegments(
        DicFileRecord file,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount,
        List<string> warnings,
        out bool canRestore,
        out bool containsForm2)
    {
        var segments = new List<SkeletonExtentSegment>();
        canRestore = true;
        containsForm2 = false;

        bool interleaved = file.FileUnitSize != 0 || file.InterleaveGapSize != 0;
        int unitBlocks = interleaved ? Math.Max(1, file.FileUnitSize) : int.MaxValue;
        int gapBlocks = interleaved ? Math.Max(0, file.InterleaveGapSize) : 0;
        long dataStartLba = interleaved && file.ExtendedAttributeRecordLength > 0
            ? checked(file.ExtentLba + unitBlocks + gapBlocks)
            : checked(file.ExtentLba + Math.Max(0, file.ExtendedAttributeRecordLength));
        if (file.DataLength == 0)
            return segments;

        if (dataStartLba < 0 || dataStartLba >= sectorCount)
        {
            warnings.Add($"'{file.Path}' data starts outside the data track at LBA {dataStartLba:N0} and cannot be restored automatically.");
            canRestore = false;
            return segments;
        }

        if (interleaved && file.FileUnitSize <= 0)
        {
            warnings.Add($"'{file.Path}' has an Interleave Gap Size but File Unit Size is zero; treating the file unit as one logical block.");
        }

        long remaining = file.DataLength;
        long currentLba = dataStartLba;
        while (remaining > 0)
        {
            long unitBytes;
            long physicalSectors;
            if (interleaved)
            {
                // ISO interleave units are measured in logical blocks, not raw-sector
                // payload capacity. The PVD logical block size used by DIC recovery is
                // 2048 bytes, so a unit of N blocks consumes at most N*2048 source bytes.
                unitBytes = Math.Min(remaining, checked((long)unitBlocks * CookedSectorSize));
                physicalSectors = DivideRoundUp(unitBytes, CookedSectorSize);
                if (currentLba + physicalSectors > sectorCount)
                    physicalSectors = 0;
            }
            else
            {
                unitBytes = remaining;
                physicalSectors = CountPhysicalSectors(currentLba, unitBytes, sectorLayouts, defaultMode, sectorCount);
            }

            if (physicalSectors <= 0 || unitBytes <= 0)
            {
                warnings.Add($"Could not map '{file.Path}' beginning at LBA {currentLba:N0} onto the DIC sector layout.");
                canRestore = false;
                break;
            }

            bool segmentForm2 = RegionContainsForm2(currentLba, physicalSectors, sectorLayouts, defaultMode, sectorCount);
            containsForm2 |= segmentForm2;
            segments.Add(new SkeletonExtentSegment(
                checked((uint)currentLba),
                unitBytes,
                physicalSectors,
                segmentForm2));

            remaining -= unitBytes;
            if (remaining > 0 && interleaved)
                currentLba = checked(currentLba + unitBlocks + gapBlocks);
            else
                currentLba = checked(currentLba + physicalSectors);
        }

        return segments;
    }

    private static IReadOnlyList<SkeletonContentEntry> BuildContentEntries(
        IReadOnlyList<DicFileRecord> files,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount,
        List<string> warnings)
    {
        var result = new List<SkeletonContentEntry>(files.Count);

        foreach (IGrouping<string, DicFileRecord> pathGroup in files
                     .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Min(file => file.Sequence)))
        {
            DicFileRecord[] sections = pathGroup.OrderBy(file => file.Sequence).ToArray();
            bool isMultiExtent = sections.Any(file => (file.Flags & IsoDirectoryRecordFlags.MultiExtent) != 0);

            if (!isMultiExtent)
            {
                foreach (DicFileRecord file in sections)
                    result.Add(BuildSingleContentEntry(file, sectorLayouts, defaultMode, sectorCount, warnings));
                continue;
            }

            bool chainValid = sections.Length >= 2;
            for (int i = 0; i < sections.Length; i++)
            {
                bool continuation = (sections[i].Flags & IsoDirectoryRecordFlags.MultiExtent) != 0;
                if (i < sections.Length - 1 && !continuation)
                    chainValid = false;
                if (i == sections.Length - 1 && continuation)
                    chainValid = false;
            }

            if (!chainValid)
            {
                warnings.Add(
                    $"'{pathGroup.Key}' has an invalid ISO 9660 Multi-Extent record chain. " +
                    "All sections except the final one must carry File Flags 0x80. Automatic restoration is disabled for this entry.");
            }

            var segments = new List<SkeletonExtentSegment>(sections.Length);
            long totalLength = 0;
            long totalPhysicalSectors = 0;
            bool containsForm2 = false;
            bool canRestore = chainValid;

            foreach (DicFileRecord section in sections)
            {
                IReadOnlyList<SkeletonExtentSegment> sectionSegments = BuildDataSegments(
                    section, sectorLayouts, defaultMode, sectorCount, warnings,
                    out bool sectionCanRestore, out bool sectionContainsForm2);
                if (!sectionCanRestore)
                    canRestore = false;
                containsForm2 |= sectionContainsForm2;
                segments.AddRange(sectionSegments);
                totalLength = checked(totalLength + section.DataLength);
                totalPhysicalSectors = checked(totalPhysicalSectors + sectionSegments.Sum(segment => segment.PhysicalSectorCount));
            }

            string[] aliases = sections
                .SelectMany(section => (section.Aliases ?? Array.Empty<string>()).Append(section.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DateTimeOffset? recordingTime = sections.Select(section => section.RecordingTime).FirstOrDefault(value => value is not null);

            result.Add(new SkeletonContentEntry(
                sections[0].Path,
                segments.Count > 0 ? segments[0].ExtentLba : checked((uint)Math.Max(0, sections[0].ExtentLba + Math.Max(0, sections[0].ExtendedAttributeRecordLength))),
                totalLength,
                null,
                null,
                SkeletonSpecialKind.None,
                canRestore,
                RequiresSource: totalLength > 0,
                PhysicalSectorCount: totalPhysicalSectors,
                PathAliases: aliases,
                RecordingTime: recordingTime,
                ContainsMode2Form2: containsForm2,
                Extents: segments,
                IsoOriginalPath: sections[0].OriginalPath ?? sections[0].Path,
                IsoRecordExtentLba: checked((uint)Math.Max(0, sections[0].ExtentLba)),
                IsoFileFlags: (byte)(sections[^1].Flags & ~IsoDirectoryRecordFlags.MultiExtent)));

            warnings.Add(
                $"ISO 9660 Multi-Extent file '{sections[0].Path}' combines {sections.Length:N0} file section(s), " +
                $"{totalLength:N0} logical bytes total. One ordinary source file will be split across those extents during resurrection.");
        }

        return result;
    }

    private static SkeletonContentEntry BuildSingleContentEntry(
        DicFileRecord file,
        IReadOnlyDictionary<long, DicSectorLayout> sectorLayouts,
        int defaultMode,
        long sectorCount,
        List<string> warnings)
    {
        IReadOnlyList<SkeletonExtentSegment> segments = BuildDataSegments(
            file,
            sectorLayouts,
            defaultMode,
            sectorCount,
            warnings,
            out bool canRestore,
            out bool containsForm2);

        bool interleaved = file.FileUnitSize != 0 || file.InterleaveGapSize != 0;
        int unitBlocks = interleaved ? Math.Max(1, file.FileUnitSize) : 0;
        int gapBlocks = interleaved ? Math.Max(0, file.InterleaveGapSize) : 0;
        long dataStartLba = interleaved && file.ExtendedAttributeRecordLength > 0
            ? checked(file.ExtentLba + unitBlocks + gapBlocks)
            : checked(file.ExtentLba + Math.Max(0, file.ExtendedAttributeRecordLength));
        long physicalSectors = segments.Sum(segment => segment.PhysicalSectorCount);
        bool exposeSegments = segments.Count > 1 || file.FileUnitSize != 0 || file.InterleaveGapSize != 0;

        return new SkeletonContentEntry(
            file.Path,
            segments.Count > 0 ? segments[0].ExtentLba : checked((uint)Math.Max(0, dataStartLba)),
            file.DataLength,
            null,
            null,
            SkeletonSpecialKind.None,
            canRestore,
            RequiresSource: file.DataLength > 0,
            PhysicalSectorCount: physicalSectors,
            PathAliases: file.Aliases,
            RecordingTime: file.RecordingTime,
            ContainsMode2Form2: containsForm2,
            Extents: exposeSegments ? segments : null,
            IsoOriginalPath: file.OriginalPath ?? file.Path,
            IsoRecordExtentLba: checked((uint)Math.Max(0, file.ExtentLba)),
            IsoFileFlags: (byte)file.Flags);
    }

    private static IReadOnlyList<DicFileSlackRegion> FindFileTailSlackRegions(
        IReadOnlyList<SkeletonContentEntry> entries,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        int defaultMode,
        long sectorCount)
    {
        var result = new List<DicFileSlackRegion>();

        foreach (SkeletonContentEntry entry in entries)
        {
            if (entry.DataLength <= 0)
                continue;

            IReadOnlyList<SkeletonExtentSegment> segments = entry.Extents is { Count: > 0 }
                ? entry.Extents
                : new[]
                {
                    new SkeletonExtentSegment(
                        entry.ExtentLba,
                        entry.DataLength,
                        entry.PhysicalSectorCount > 0
                            ? entry.PhysicalSectorCount
                            : CountPhysicalSectors(entry.ExtentLba, entry.DataLength, layouts, defaultMode, sectorCount),
                        entry.ContainsMode2Form2)
                };

            foreach (SkeletonExtentSegment segment in segments)
            {
                long remaining = segment.DataLength;
                for (long sectorOffset = 0; sectorOffset < segment.PhysicalSectorCount && remaining > 0; sectorOffset++)
                {
                    long lba = (long)segment.ExtentLba + sectorOffset;
                    if (lba < 0 || lba >= sectorCount)
                        break;

                    int capacity = GetPayloadCapacity(GetLayout(layouts, lba, defaultMode));
                    int used = (int)Math.Min((long)capacity, remaining);
                    remaining -= used;
                    if (remaining == 0 && used < capacity)
                    {
                        result.Add(new DicFileSlackRegion(
                            entry.Path,
                            lba,
                            capacity - used,
                            capacity,
                            segment.ContainsMode2Form2));
                    }
                }
            }
        }

        return result
            .GroupBy(item => (item.Path, item.Lba, item.SlackBytes))
            .Select(group => group.First())
            .OrderBy(item => item.Lba)
            .ToArray();
    }

    private static IReadOnlyList<DicUnclaimedSectorRegion> FindUnclaimedVolumeRegions(
        DicVolumeInfo volume,
        long sectorCount,
        IReadOnlyList<SkeletonContentEntry> entries,
        IEnumerable<long> populatedMetadataLbas)
    {
        long volumeEndExclusive = volume.VolumeSpaceSize > 0
            ? Math.Min(volume.VolumeSpaceSize, sectorCount)
            : sectorCount;
        if (volumeEndExclusive <= 16)
            return Array.Empty<DicUnclaimedSectorRegion>();

        var claimed = new HashSet<long>();
        for (long lba = 0; lba < Math.Min(16, volumeEndExclusive); lba++)
            claimed.Add(lba);

        foreach (long lba in volume.MetadataLbas.Concat(populatedMetadataLbas))
        {
            if (lba >= 0 && lba < volumeEndExclusive)
                claimed.Add(lba);
        }

        foreach (SkeletonContentEntry entry in entries)
        {
            IReadOnlyList<SkeletonExtentSegment> segments = entry.Extents is { Count: > 0 }
                ? entry.Extents
                : new[]
                {
                    new SkeletonExtentSegment(
                        entry.ExtentLba,
                        entry.DataLength,
                        entry.PhysicalSectorCount,
                        entry.ContainsMode2Form2)
                };

            foreach (SkeletonExtentSegment segment in segments)
            {
                long count = segment.PhysicalSectorCount;
                if (count <= 0 && segment.DataLength > 0)
                    count = DivideRoundUp(segment.DataLength, CookedSectorSize);

                long start = segment.ExtentLba;
                long endExclusive = Math.Min(volumeEndExclusive, checked(start + Math.Max(0, count)));
                for (long lba = Math.Max(0, start); lba < endExclusive; lba++)
                    claimed.Add(lba);
            }
        }

        long[] unclaimed = Enumerable.Range(16, checked((int)Math.Max(0, volumeEndExclusive - 16)))
            .Select(value => (long)value)
            .Where(lba => !claimed.Contains(lba))
            .ToArray();

        return GroupContiguousLbas(unclaimed)
            .Select(region => new DicUnclaimedSectorRegion(region.Start, region.Start + region.Count - 1))
            .ToArray();
    }

}
