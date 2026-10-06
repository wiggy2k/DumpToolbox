using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private const string ToastIsoBuilderSignature = "TOAST ISO 9660 BUILDER";

    private static readonly string[] KnownToastHybridPartitionNames =
    [
        "TOAST 2.5 Partition",
        "Toast 3.0 PPC Partition",
        "Toast 3.0.2 HFS Optimizer",
        "Toast 3.0.5 PPC Partition",
        "Toast 3.0.5 PPC HFS Optimizer",
        "Toast 3.5 PPC Partition",
        "Toast 3.5 PPC HFS Optimizer",
        "Toast 3.5.1 PPC Partition",
        "Toast 3.5.2 PPC Partition",
        "Toast 3.5.2 PPC HFS Optimizer",
        "Toast 3.5.3 PPC Partition",
        "Toast 3.5.3 PPC HFS Optimizer",
        "Toast 3.5.4 Partition",
        "Toast 3.5.4 HFS Optimizer",
        "Toast 3.5.4 PPC Partition",
        "Toast 3.5.4 PPC HFS Optimizer",
        "Toast 3.5.5 PPC Partition",
        "Toast 3.5.6 PPC HFS Optimizer",
        "Toast 3.5.7 PPC Partition",
        "Toast 3.5.7 PPC HFS Optimizer",
        "Toast 3.8 PPC Partition",
        "Toast 4.0 PPC Partition",
        "Toast 4.0.2 PPC HFS Optimizer",
        "Toast 4.1 Partition",
        "Toast 4.1.2 Partition",
        "Toast 4.1.2 HFS Optimizer",
        "Toast 4.1.3 HFS Optimizer",
        "Toast 5.0 Partition",
        "Toast F-5.0.1 HFS Optimizer",
        "Toast 5.0.1 HFS/Joliet Builder",
        "Toast 5.0.2 HFS Optimizer",
        "Toast 5.1.1 HFS/Joliet Builder",
        "Toast 5.2 HFS Optimizer",
        "Toast 5.2 HFS+/Joliet Builder",
        "Toast 5.2.3 HFS Optimizer",
        "Toast 6.0.3 HFS Optimizer",
        "Toast 6.0.3 HFS+/Joliet Builder",
        "Toast 7.0 HFS Optimizer",
        "Toast 9.0.1 HFS Optimizer",
        "Toast 9.0.1 HFS+/Joliet Builder",
        "Toast 9.0.5 HFS Optimizer"
    ];

    private static KnownSystemAreaRecoveryInfo? TryBuildToastHybridSystemArea(
        ToastHybridRecoveryAssessment assessment,
        int imageBaseLba,
        string expectedSha1)
    {
        long absoluteStart64 = checked(
            assessment.HfsPartitionStartLba * 4 + assessment.HfsPartitionStartByteOffset / 512);
        long relativeStart64 = checked(
            (assessment.HfsPartitionStartLba - imageBaseLba) * 4 +
            assessment.HfsPartitionStartByteOffset / 512);
        if (absoluteStart64 < 4 || absoluteStart64 > uint.MaxValue ||
            relativeStart64 < 4 || relativeStart64 > uint.MaxValue)
        {
            return null;
        }

        foreach (uint partitionStart in new[] { (uint)absoluteStart64, (uint)relativeStart64 }.Distinct())
        {
            uint ddmBlockCount = checked(partitionStart + assessment.HfsPartitionBlockCount);
            foreach (string partitionName in KnownToastHybridPartitionNames)
            foreach (uint status in new uint[] { 0x13, 0x33 })
            foreach (uint partitionMapBlocks in new uint[]
                     {
                         2,
                         35,
                         checked((partitionStart + 3) / 4)
                     }.Distinct())
            {
                byte[] payload = new byte[SystemAreaSectors * CookedSectorSize];
                Span<byte> sector = payload.AsSpan(0, CookedSectorSize);
                WriteDriverDescriptor(sector, ddmBlockCount);
                BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(8, 2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(sector.Slice(10, 2), 1);

                Span<byte> mapEntry = sector.Slice(512, 512);
                WriteApplePartitionEntry(
                    mapEntry,
                    2,
                    1,
                    partitionMapBlocks,
                    "MRKS",
                    "Apple_partition_map",
                    status);
                BinaryPrimitives.WriteUInt32BigEndian(mapEntry.Slice(84, 4), partitionMapBlocks);

                Span<byte> hfsEntry = sector.Slice(1024, 512);
                WriteApplePartitionEntry(
                    hfsEntry,
                    2,
                    partitionStart,
                    assessment.HfsPartitionBlockCount,
                    partitionName,
                    "Apple_HFS",
                    status);
                BinaryPrimitives.WriteUInt32BigEndian(
                    hfsEntry.Slice(84, 4),
                    assessment.HfsPartitionBlockCount);

                KnownSystemAreaRecoveryInfo? match = MatchKnownPayload(
                    payload,
                    expectedSha1,
                    $"Toast hybrid Apple partition map; {partitionName}; HFS blocks " +
                    $"{partitionStart:N0}-{ddmBlockCount:N0}");
                if (match is not null)
                    return match;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<KnownGapRecoveryInfo>> AddToastHybridGapRecoveriesAsync(
        SkeletonImageReader image,
        IReadOnlyList<SkeletonContentEntry> entries,
        ToastHybridRecoveryAssessment? assessment,
        IReadOnlyList<KnownGapRecoveryInfo> existing,
        CancellationToken cancellationToken)
    {
        if (assessment is null)
            return existing;

        var result = existing.ToList();
        long partitionStartByte = checked(
            assessment.HfsPartitionStartLba * CookedSectorSize + assessment.HfsPartitionStartByteOffset);
        long partitionEndByte = checked(
            partitionStartByte + (long)assessment.HfsPartitionBlockCount * 512);

        // Across the checked Toast 7/9 builder/optimizer images, the HFS partition
        // is followed by exactly 152 zero logical sectors. The generic zero-GAP
        // rule has already proved the bytes through the manifest hash; attach the
        // mastering-specific explanation when its geometry agrees too. Earlier
        // Toast versions can receive the label only when both checks also pass.
        if (partitionEndByte % CookedSectorSize == 0)
        {
            long tailStartLba = partitionEndByte / CookedSectorSize;
            for (int index = 0; index < result.Count; index++)
            {
                KnownGapRecoveryInfo info = result[index];
                if (info.StartLba != tailStartLba ||
                    info.SectorCount != 152 ||
                    info.Form1SectorCount != 152 ||
                    info.Form2SectorCount != 0 ||
                    info.PreservedSectorCount != 0 ||
                    info.GeneratedFill55Lbas.Count != 0 ||
                    info.GeneratedPayload is not null)
                {
                    continue;
                }

                result[index] = info with
                {
                    PatternName = "Toast 152-sector zero post-HFS tail"
                };
            }
        }

        if (result.Any(info => info.Path.Equals(assessment.GapPath, StringComparison.OrdinalIgnoreCase)))
            return result;

        SkeletonContentEntry? gap = entries.FirstOrDefault(entry =>
            entry.SpecialKind == SkeletonSpecialKind.Gap &&
            entry.Path.Equals(assessment.GapPath, StringComparison.OrdinalIgnoreCase) &&
            entry.CanRestore &&
            entry.DataLength > 0 &&
            entry.DataLength % CookedSectorSize == 0 &&
            entry.Sha1 is { Length: 40 } &&
            string.IsNullOrWhiteSpace(entry.XaSha1));
        if (gap is null)
            return result;

        long primaryMdbByte = checked(partitionStartByte + 1024);
        long alternateMdbByte = checked(partitionEndByte - 1024);
        long gapStartByte = checked((long)gap.ExtentLba * CookedSectorSize);
        long gapEndByte = checked(gapStartByte + gap.DataLength);
        if (primaryMdbByte < gapStartByte || primaryMdbByte + 512 > gapEndByte)
            return result;

        byte[] alternateSector;
        try
        {
            alternateSector = await image.ReadForm1SectorAsync(
                alternateMdbByte / CookedSectorSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            return result;
        }

        int alternateOffset = checked((int)(alternateMdbByte % CookedSectorSize));
        if (alternateOffset + 512 > alternateSector.Length ||
            ReadToastBeUInt16(alternateSector, alternateOffset) != 0x4244)
        {
            return result;
        }

        byte[] candidate = new byte[checked((int)gap.DataLength)];
        long sectorCount = gap.DataLength / CookedSectorSize;
        try
        {
            for (long sectorOffset = 0; sectorOffset < sectorCount; sectorOffset++)
            {
                byte[] payload = await image.ReadForm1SectorAsync(
                    checked((long)gap.ExtentLba + sectorOffset),
                    cancellationToken).ConfigureAwait(false);
                payload.CopyTo(candidate, checked((int)(sectorOffset * CookedSectorSize)));
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            return result;
        }

        int primaryOffset = checked((int)(primaryMdbByte - gapStartByte));
        bool primaryIsZero = true;
        bool primaryMatchesAlternate = true;
        for (int index = 0; index < 512; index++)
        {
            byte primaryByte = candidate[primaryOffset + index];
            byte alternateByte = alternateSector[alternateOffset + index];
            primaryIsZero &= primaryByte == 0;
            primaryMatchesAlternate &= primaryByte == alternateByte;
        }
        if (!primaryIsZero && !primaryMatchesAlternate)
            return result;
        Buffer.BlockCopy(alternateSector, alternateOffset, candidate, primaryOffset, 512);

        string actualSha1 = Convert.ToHexString(SHA1.HashData(candidate)).ToLowerInvariant();
        if (!actualSha1.Equals(gap.Sha1, StringComparison.OrdinalIgnoreCase))
            return result;

        result.Add(new KnownGapRecoveryInfo(
            gap.Path,
            "Toast classic-HFS primary MDB copied from the surviving alternate MDB",
            actualSha1,
            null,
            gap.ExtentLba,
            sectorCount,
            sectorCount,
            0,
            0,
            new HashSet<long>(),
            candidate));
        return result;
    }

    private static async Task<ToastHybridRecoveryAssessment?> TryAssessToastHybridRecoveryAsync(
        SkeletonImageReader image,
        IsoTree isoTree,
        IReadOnlyList<SkeletonContentEntry> entries,
        CancellationToken cancellationToken)
    {
        if (!isoTree.ApplicationIdentifier.Contains(ToastIsoBuilderSignature, StringComparison.OrdinalIgnoreCase))
            return null;

        SkeletonContentEntry[] gaps = entries
            .Where(entry => entry.SpecialKind == SkeletonSpecialKind.Gap && entry.DataLength > 0)
            .ToArray();
        if (gaps.Length == 0 || isoTree.Files.Count == 0)
            return null;

        long volumeEndLba = isoTree.VolumeSpaceSize > image.SectorCount
            ? isoTree.VolumeSpaceSize
            : checked((long)image.BaseLba + isoTree.VolumeSpaceSize);
        long alternateMdbLba = volumeEndLba - 1;
        if (alternateMdbLba < image.BaseLba || alternateMdbLba >= image.BaseLba + image.SectorCount)
            return null;

        byte[] finalVolumeSector;
        try
        {
            finalVolumeSector = await image.ReadForm1SectorAsync(alternateMdbLba, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            return null;
        }

        for (int quarter = 0; quarter < 4; quarter++)
        {
            int offset = quarter * 512;
            if (ReadToastBeUInt16(finalVolumeSector, offset) != 0x4244)
                continue;

            ushort allocationBlockCount = ReadToastBeUInt16(finalVolumeSector, offset + 18);
            uint allocationBlockSize = ReadToastBeUInt32(finalVolumeSector, offset + 20);
            ushort firstAllocationBlock = ReadToastBeUInt16(finalVolumeSector, offset + 28);
            uint hfsFileCount = ReadToastBeUInt32(finalVolumeSector, offset + 84);
            uint hfsDirectoryCount = ReadToastBeUInt32(finalVolumeSector, offset + 88);
            uint catalogFileSize = ReadToastBeUInt32(finalVolumeSector, offset + 146);
            ushort catalogStartAllocationBlock = ReadToastBeUInt16(finalVolumeSector, offset + 150);
            if (allocationBlockCount == 0 ||
                allocationBlockSize < 512 ||
                allocationBlockSize % 512 != 0 ||
                allocationBlockSize > 1024 * 1024)
            {
                continue;
            }

            int nameLength = Math.Min(finalVolumeSector[offset + 36], (byte)27);
            string hfsVolumeName = Encoding.Latin1.GetString(finalVolumeSector, offset + 37, nameLength);
            uint allocationScaleBlocks = allocationBlockSize / 512;
            ulong allocationSpanBlocks = checked(
                (ulong)firstAllocationBlock + (ulong)allocationBlockCount * allocationScaleBlocks);
            long hfsEndAppleBlock = checked(alternateMdbLba * 4 + quarter + 2);
            long firstFileLba = isoTree.Files.Min(file => (long)file.Lba);
            long firstFileAppleBlock = checked(firstFileLba * 4);
            int metadataAllocationEnd = Math.Max(
                ReadHfsExtentEnd(finalVolumeSector, offset + 134),
                ReadHfsExtentEnd(finalVolumeSector, offset + 150));

            ToastHybridCandidate? best = null;
            // Toast classic-HFS builders leave only a small reserved tail after the
            // allocation area. Trying that bounded family is structural inference;
            // it is never accepted as recovered content without the manifest hash.
            for (int tailBlocks = 2; tailBlocks <= 32; tailBlocks++)
            {
                ulong partitionBlocks64 = allocationSpanBlocks + (uint)tailBlocks;
                if (partitionBlocks64 > uint.MaxValue || partitionBlocks64 >= (ulong)hfsEndAppleBlock)
                    continue;

                uint partitionBlocks = (uint)partitionBlocks64;
                long partitionStartBlock = hfsEndAppleBlock - partitionBlocks;
                long allocationStartBlock = partitionStartBlock + firstAllocationBlock;
                long deltaToFirstFile = firstFileAppleBlock - allocationStartBlock;
                if (deltaToFirstFile < 0 || deltaToFirstFile % allocationScaleBlocks != 0)
                    continue;

                long firstSharedAllocationBlock = deltaToFirstFile / allocationScaleBlocks;
                long unexplainedAllocationBlocks = Math.Max(0, firstSharedAllocationBlock - metadataAllocationEnd);
                long unexplainedSectors = checked(
                    unexplainedAllocationBlocks * allocationBlockSize / CookedSectorSize);
                long partitionStartLba = partitionStartBlock / 4;
                int partitionStartByteOffset = checked((int)(partitionStartBlock % 4) * 512);
                long primaryMdbByte = checked(partitionStartLba * CookedSectorSize + partitionStartByteOffset + 1024);
                long catalogStartBlock = checked(
                    allocationStartBlock + (long)catalogStartAllocationBlock * allocationScaleBlocks);
                long catalogStartLba = catalogStartBlock / 4;
                int catalogStartByteOffset = checked((int)(catalogStartBlock % 4) * 512);
                long catalogStartByte = checked(catalogStartLba * CookedSectorSize + catalogStartByteOffset);

                SkeletonContentEntry? containingGap = gaps.FirstOrDefault(gap =>
                {
                    long gapStartByte = checked((long)gap.ExtentLba * CookedSectorSize);
                    long gapEndByte = checked(gapStartByte + gap.DataLength);
                    return primaryMdbByte >= gapStartByte && primaryMdbByte + 512 <= gapEndByte;
                });
                if (containingGap is null)
                    continue;

                long containingGapStartByte = checked((long)containingGap.ExtentLba * CookedSectorSize);
                long containingGapEndByte = checked(containingGapStartByte + containingGap.DataLength);
                bool catalogCoveredByGap = catalogFileSize > 0 &&
                    catalogStartByte >= containingGapStartByte &&
                    catalogStartByte + catalogFileSize <= containingGapEndByte;

                var candidate = new ToastHybridCandidate(
                    containingGap,
                    partitionStartLba,
                    partitionStartByteOffset,
                    partitionBlocks,
                    unexplainedSectors,
                    catalogStartLba,
                    catalogStartByteOffset,
                    catalogCoveredByGap);
                if (best is null || candidate.UnexplainedAllocatedSectors < best.UnexplainedAllocatedSectors)
                    best = candidate;
            }

            if (best is null)
                continue;

            if (hfsFileCount > int.MaxValue || hfsDirectoryCount > int.MaxValue)
                continue;
            int extraHfsFiles = Math.Max(0, (int)hfsFileCount - isoTree.Files.Count);
            int extraHfsDirectories = Math.Max(0, (int)hfsDirectoryCount - isoTree.DirectoryCount);
            bool canReconstructExactly =
                hfsFileCount == isoTree.Files.Count &&
                hfsDirectoryCount == isoTree.DirectoryCount &&
                best.UnexplainedAllocatedSectors == 0;

            string summary = canReconstructExactly
                ? $"classic HFS volume '{hfsVolumeName}' is completely accounted for by the ISO tree; " +
                  "the Toast metadata region is eligible for hash-verified synthesis"
                : $"classic HFS volume '{hfsVolumeName}' has {hfsFileCount:N0} file(s) and {hfsDirectoryCount:N0} directory entry/entries, " +
                  $"versus {isoTree.Files.Count:N0} file(s) and {isoTree.DirectoryCount:N0} directory entry/entries in ISO; " +
                  $"{extraHfsFiles:N0} HFS-only file(s), {extraHfsDirectories:N0} HFS-only directory entry/entries, and " +
                  $"{best.UnexplainedAllocatedSectors:N0} allocated sector(s) before the first shared file are not derivable from the skeleton";

            return new ToastHybridRecoveryAssessment(
                best.Gap.Path,
                hfsVolumeName,
                best.PartitionStartLba,
                best.PartitionStartByteOffset,
                best.PartitionBlockCount,
                allocationBlockCount,
                allocationBlockSize,
                hfsFileCount,
                hfsDirectoryCount,
                isoTree.Files.Count,
                isoTree.DirectoryCount,
                best.CatalogStartLba,
                best.CatalogStartByteOffset,
                catalogFileSize,
                best.CatalogCoveredByGap,
                firstFileLba,
                best.UnexplainedAllocatedSectors,
                canReconstructExactly,
                summary);
        }

        return null;
    }

    private static int ReadHfsExtentEnd(byte[] mdbSector, int extentOffset)
    {
        int end = 0;
        for (int index = 0; index < 3; index++)
        {
            int offset = extentOffset + index * 4;
            ushort start = ReadToastBeUInt16(mdbSector, offset);
            ushort count = ReadToastBeUInt16(mdbSector, offset + 2);
            end = Math.Max(end, start + count);
        }
        return end;
    }

    private static ushort ReadToastBeUInt16(byte[] data, int offset) =>
        checked((ushort)((data[offset] << 8) | data[offset + 1]));

    private static uint ReadToastBeUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private sealed record ToastHybridCandidate(
        SkeletonContentEntry Gap,
        long PartitionStartLba,
        int PartitionStartByteOffset,
        uint PartitionBlockCount,
        long UnexplainedAllocatedSectors,
        long CatalogStartLba,
        int CatalogStartByteOffset,
        bool CatalogCoveredByGap);
}
