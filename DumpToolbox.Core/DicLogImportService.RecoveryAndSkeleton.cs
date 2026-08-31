using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private static IReadOnlyList<SkeletonDonorRequirement> BuildOptionalExactnessDonorRequirements(
        DicVolumeInfo volume,
        long sectorCount,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        int defaultMode,
        IReadOnlyList<DicFileSlackRegion> slackRegions,
        IReadOnlyList<DicUnclaimedSectorRegion> unclaimedRegions,
        IReadOnlyDictionary<long, DicPayloadEvidence> offsetEvidence,
        IReadOnlySet<long> synthesizedMetadataLbas,
        IReadOnlySet<long> missingMetadataLbas)
    {
        var requirements = new List<SkeletonDonorRequirement>();

        foreach (DicFileSlackRegion slack in slackRegions)
        {
            requirements.Add(new SkeletonDonorRequirement(
                $"<file slack: {slack.Path}>",
                checked((uint)slack.Lba),
                0,
                1,
                slack.ContainsMode2Form2,
                0,
                0,
                0,
                0,
                $"file tail slack ({slack.SlackBytes:N0} byte(s) after logical EOF of {slack.Path})",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        foreach (DicUnclaimedSectorRegion region in unclaimedRegions)
        {
            requirements.Add(new SkeletonDonorRequirement(
                $"<unclaimed in-volume sectors LBA {region.StartLba:N0}-{region.EndLba:N0}>",
                checked((uint)region.StartLba),
                0,
                region.SectorCount,
                RegionContainsForm2(region.StartLba, region.SectorCount, layouts, defaultMode, sectorCount),
                0,
                0,
                0,
                0,
                "in-volume sectors not described by ISO9660 file extents or DIC-preserved metadata (may contain hybrid/HFS or mastering data)",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        long systemSectorCount = Math.Min(16, sectorCount);
        long[] unprovenSystemLbas = Enumerable.Range(0, checked((int)systemSectorCount))
            .Select(value => (long)value)
            .Where(lba => !offsetEvidence.TryGetValue(lba, out DicPayloadEvidence? evidence) || !evidence.IsComplete)
            .ToArray();
        foreach ((long start, long count) in GroupContiguousLbas(unprovenSystemLbas))
        {
            requirements.Add(new SkeletonDonorRequirement(
                $"<unproven ISO system area LBA {start:N0}-{start + count - 1:N0}>",
                checked((uint)start),
                0,
                count,
                RegionContainsForm2(start, count, layouts, defaultMode, sectorCount),
                0,
                0,
                0,
                0,
                "unproven ISO system-area payload bytes",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        foreach ((long start, long count) in GroupContiguousLbas(synthesizedMetadataLbas.OrderBy(value => value)))
        {
            requirements.Add(new SkeletonDonorRequirement(
                $"<synthesized ISO path-table metadata LBA {start:N0}-{start + count - 1:N0}>",
                checked((uint)start),
                0,
                count,
                RegionContainsForm2(start, count, layouts, defaultMode, sectorCount),
                0,
                0,
                0,
                0,
                "synthesized ISO path-table sector/padding",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        foreach ((long start, long count) in GroupContiguousLbas(missingMetadataLbas.OrderBy(value => value)))
        {
            requirements.Add(new SkeletonDonorRequirement(
                $"<missing DIC ISO metadata LBA {start:N0}-{start + count - 1:N0}>",
                checked((uint)start),
                0,
                count,
                RegionContainsForm2(start, count, layouts, defaultMode, sectorCount),
                0,
                0,
                0,
                0,
                "ISO9660 metadata not completely dumped by mainInfo",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        if (volume.VolumeSpaceSize > 0 && sectorCount > volume.VolumeSpaceSize)
        {
            long start = volume.VolumeSpaceSize;
            long count = sectorCount - start;
            requirements.Add(new SkeletonDonorRequirement(
                $"<post-volume sectors LBA {start:N0}-{sectorCount - 1:N0}>",
                checked((uint)start),
                0,
                count,
                RegionContainsForm2(start, count, layouts, defaultMode, sectorCount),
                0,
                0,
                0,
                0,
                "sectors after ISO9660 Volume Space Size",
                RequireRecordMatch: false,
                BlocksResurrection: false));
        }

        return requirements;
    }

    private static IReadOnlyList<SkeletonDonorRequirement> BuildEccEdcExactnessDonorRequirements(
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        IReadOnlyList<long> eccErrorPhysicalLbas,
        IReadOnlySet<long> knownFinalRecipeLbas,
        long sectorCount)
    {
        var reasonsByLba = new Dictionary<long, string>();

        foreach (KeyValuePair<long, DicSectorLayout> pair in layouts)
        {
            long lba = pair.Key;
            DicSectorLayout layout = pair.Value;
            if (lba < 0 || lba >= sectorCount)
                continue;
            if (knownFinalRecipeLbas.Contains(lba))
                continue;

            string? reason = null;
            if (layout.IsAudio)
                reason = "opaque audio sector identified by DIC EccEdc";
            else if (layout.IsUnknown)
                reason = "sector with unknown/unsafe final mode classification";
            else if (layout.HasBlockIndicators)
                reason = "Mode byte has Block Indicators but EccEdc does not expose the exact upper bits";
            else if (layout.HasMissingMsf)
                reason = "data-sector EccEdc record does not expose an MSF/header field";
            else if (layout.SummaryInvalidMode && !layout.HasInvalidMode)
                reason = "EccEdc summary flags an invalid Mode byte but the exact raw byte is not printed";
            else if (layout.HasInvalidSync || layout.HasZeroSync || layout.SummaryInvalidSync || layout.SummaryZeroSync)
                reason = "non-canonical sync pattern requires exact raw-sector evidence";
            else if (layout.XaSubheaderCopiesDiffer)
                reason = "two logged XA subheader copies differ; exact raw content is required because their Form semantics may disagree";
            else if (layout.SummarySubheaderMismatch && !layout.XaSubheaderCopiesDiffer)
                reason = "EccEdc summary flags unequal XA subheader copies but the exact eight raw bytes were not recovered from the per-sector line";
            else if (layout.SummaryExpectedZeroMismatch)
                reason = "EccEdc flags a sector that differs from an expected all-zero pattern; exact final bytes are not inferable from the summary alone";
            else if (layout.HasEccMismatch && !knownFinalRecipeLbas.Contains(lba))
                reason = "per-sector DIC EccEdc record identifies an ECC/EDC mismatch but no proven byte-level reproduction recipe is known";

            if (reason is not null)
                reasonsByLba[lba] = reason;
        }

        foreach (long lba in eccErrorPhysicalLbas)
        {
            if (lba < 0 || lba >= sectorCount || knownFinalRecipeLbas.Contains(lba))
                continue;
            reasonsByLba.TryAdd(lba, "DIC EccEdc identifies an ECC/EDC mismatch but no proven byte-level reproduction recipe is known");
        }

        var requirements = new List<SkeletonDonorRequirement>();
        foreach (IGrouping<string, KeyValuePair<long, string>> reasonGroup in reasonsByLba
                     .OrderBy(pair => pair.Key)
                     .GroupBy(pair => pair.Value, StringComparer.Ordinal))
        {
            long[] lbas = reasonGroup.Select(pair => pair.Key).OrderBy(value => value).ToArray();
            foreach ((long start, long count) in GroupContiguousLbas(lbas))
            {
                requirements.Add(new SkeletonDonorRequirement(
                    $"<DIC exact raw evidence LBA {start:N0}-{start + count - 1:N0}>",
                    checked((uint)start),
                    0,
                    count,
                    ContainsMode2Form2: false,
                    FileFlags: 0,
                    ExtendedAttributeRecordLength: 0,
                    FileUnitSize: 0,
                    InterleaveGapSize: 0,
                    Reason: reasonGroup.Key,
                    RequireRecordMatch: false,
                    BlocksResurrection: false,
                    RequiresRawDonor: true));
            }
        }

        return requirements;
    }

    private static IReadOnlyList<DicRecoveryCoverageItem> BuildRecoveryCoverageAudit(
        DicVolumeInfo volume,
        long sectorCount,
        IReadOnlyList<SkeletonContentEntry> entries,
        IReadOnlyList<DicFileSlackRegion> slackRegions,
        IReadOnlyList<DicUnclaimedSectorRegion> unclaimedRegions,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        int defaultMode,
        IReadOnlySet<long> exactMainInfoLbas,
        IReadOnlyDictionary<long, DicPayloadEvidence> offsetEvidence,
        IReadOnlySet<long> synthesizedMetadataLbas,
        IReadOnlySet<long> missingMetadataLbas)
    {
        var audit = new List<DicRecoveryCoverageItem>();

        if (exactMainInfoLbas.Count > 0)
        {
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.ExactFromDic,
                $"Exact mainInfo ISO9660 metadata: {exactMainInfoLbas.Count:N0} complete sector(s)",
                checked((long)exactMainInfoLbas.Count * CookedSectorSize)));
        }

        long systemSectorCount = Math.Min(16, sectorCount);
        DicPayloadEvidence[] systemEvidence = offsetEvidence
            .Where(pair => pair.Key >= 0 && pair.Key < systemSectorCount)
            .Select(pair => pair.Value)
            .ToArray();
        long knownSystemBytes = 0;
        int fullyKnownSystemSectors = 0;
        for (long lba = 0; lba < systemSectorCount; lba++)
        {
            if (exactMainInfoLbas.Contains(lba))
            {
                knownSystemBytes += CookedSectorSize;
                fullyKnownSystemSectors++;
                continue;
            }
            if (offsetEvidence.TryGetValue(lba, out DicPayloadEvidence? evidence))
            {
                knownSystemBytes += evidence.KnownByteCount;
                if (evidence.IsComplete)
                    fullyKnownSystemSectors++;
            }
        }
        if (knownSystemBytes > 0)
        {
            string partialDetail = string.Join(", ", offsetEvidence
                .Where(pair => pair.Key >= 0 && pair.Key < systemSectorCount && !pair.Value.IsComplete && pair.Value.KnownByteCount > 0)
                .OrderBy(pair => pair.Key)
                .Take(6)
                .Select(pair => $"LBA {pair.Key:N0}: {pair.Value.KnownByteCount:N0}/{CookedSectorSize:N0} bytes"));
            string description = $"Drive-offset captures prove {knownSystemBytes:N0} ISO system-area payload byte(s), including {fullyKnownSystemSectors:N0} complete sector(s)";
            if (!string.IsNullOrWhiteSpace(partialDetail))
                description += $"; partial evidence: {partialDetail}";
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.ProvenBytes,
                description,
                knownSystemBytes,
                0,
                Math.Max(0, systemSectorCount - 1)));
        }

        if (synthesizedMetadataLbas.Count > 0)
        {
            long pathSectorsPerCopy = Math.Max(1, DivideRoundUp(volume.PrimaryPathTableSize, CookedSectorSize));
            long copies = Math.Max(1, synthesizedMetadataLbas.Count / pathSectorsPerCopy);
            long deterministicBytes = volume.PrimaryPathTableSize > 0
                ? Math.Min((long)synthesizedMetadataLbas.Count * CookedSectorSize, checked(volume.PrimaryPathTableSize * copies))
                : 0;
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.DeterministicSynthesis,
                $"Primary ISO9660 path-table metadata synthesized from volDesc records: {synthesizedMetadataLbas.Count:N0} sector(s)",
                deterministicBytes));

            long paddingBytes = checked((long)synthesizedMetadataLbas.Count * CookedSectorSize) - deterministicBytes;
            if (paddingBytes > 0)
            {
                audit.Add(new DicRecoveryCoverageItem(
                    DicRecoveryCoverageKind.AssumedZero,
                    $"Padding in synthesized path-table sector(s) is not present verbatim in the logs",
                    paddingBytes,
                    synthesizedMetadataLbas.Min(),
                    synthesizedMetadataLbas.Max(),
                    DonorCapable: true));
            }
        }

        long sourceBytes = entries.Where(entry => entry.RequiresSource).Sum(entry => entry.DataLength);
        if (sourceBytes > 0)
        {
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.SourcePayload,
                $"Logical file payload expected from recovered source files: {entries.Count(entry => entry.RequiresSource):N0} entry/entries",
                sourceBytes));
        }

        if (unclaimedRegions.Count > 0)
        {
            long unclaimedBytes = 0;
            foreach (DicUnclaimedSectorRegion region in unclaimedRegions)
            {
                for (long lba = region.StartLba; lba <= region.EndLba; lba++)
                {
                    if (exactMainInfoLbas.Contains(lba))
                        continue;
                    unclaimedBytes = checked(unclaimedBytes + GetPayloadCapacity(GetLayout(layouts, lba, defaultMode)));
                }
            }

            string examples = string.Join("; ", unclaimedRegions
                .Take(6)
                .Select(region => $"LBA {region.StartLba:N0}-{region.EndLba:N0}"));
            long unresolvedUnclaimedSectors = 0;
            foreach (DicUnclaimedSectorRegion region in unclaimedRegions)
            {
                for (long lba = region.StartLba; lba <= region.EndLba; lba++)
                {
                    if (!exactMainInfoLbas.Contains(lba))
                        unresolvedUnclaimedSectors++;
                }
            }
            if (unclaimedBytes > 0)
                audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.AssumedZero,
                $"In-volume sectors not claimed by recovered ISO9660 file extents or DIC-preserved metadata: {unresolvedUnclaimedSectors:N0} sector(s)" +
                (string.IsNullOrWhiteSpace(examples) ? string.Empty : $"; examples {examples}"),
                unclaimedBytes,
                unclaimedRegions.Min(region => region.StartLba),
                unclaimedRegions.Max(region => region.EndLba),
                DonorCapable: true));
        }

        long unknownSystemBytes = checked(systemSectorCount * CookedSectorSize) - knownSystemBytes;
        if (unknownSystemBytes > 0)
        {
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.AssumedZero,
                $"ISO system-area payload bytes not proven by DIC offset-test captures",
                unknownSystemBytes,
                0,
                Math.Max(0, systemSectorCount - 1),
                DonorCapable: true));
        }

        if (missingMetadataLbas.Count > 0)
        {
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.AssumedZero,
                $"Expected primary ISO9660 metadata sector(s) not completely dumped in mainInfo: {missingMetadataLbas.Count:N0}",
                checked((long)missingMetadataLbas.Count * CookedSectorSize),
                missingMetadataLbas.Min(),
                missingMetadataLbas.Max(),
                DonorCapable: true));
        }

        long slackBytes = slackRegions
            .Where(item => !exactMainInfoLbas.Contains(item.Lba))
            .Sum(item => (long)item.SlackBytes);
        if (slackBytes > 0)
        {
            string examples = string.Join("; ", slackRegions
                .Where(item => !exactMainInfoLbas.Contains(item.Lba))
                .OrderByDescending(item => item.SlackBytes)
                .Take(5)
                .Select(item => $"{item.Path}: {item.SlackBytes:N0}"));
            audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.AssumedZero,
                $"File-sector tail slack not contained in ordinary extracted files: {slackRegions.Count(item => !exactMainInfoLbas.Contains(item.Lba)):N0} sector(s)" +
                (string.IsNullOrWhiteSpace(examples) ? string.Empty : $"; examples {examples}"),
                slackBytes,
                slackRegions.Min(item => item.Lba),
                slackRegions.Max(item => item.Lba),
                DonorCapable: true));
        }

        if (volume.VolumeSpaceSize > 0 && sectorCount > volume.VolumeSpaceSize)
        {
            long start = volume.VolumeSpaceSize;
            long payloadBytes = 0;
            long unresolvedPostVolumeSectors = 0;
            for (long lba = start; lba < sectorCount; lba++)
            {
                if (exactMainInfoLbas.Contains(lba))
                    continue;
                payloadBytes = checked(payloadBytes + GetPayloadCapacity(GetLayout(layouts, lba, defaultMode)));
                unresolvedPostVolumeSectors++;
            }

            if (payloadBytes > 0)
                audit.Add(new DicRecoveryCoverageItem(
                DicRecoveryCoverageKind.AssumedZero,
                $"Track sectors after ISO9660 Volume Space Size: {unresolvedPostVolumeSectors:N0} sector(s)",
                payloadBytes,
                start,
                sectorCount - 1,
                DonorCapable: true));
        }

        return audit;
    }

    private static IReadOnlyList<(long Start, long Count)> GroupContiguousLbas(IEnumerable<long> lbas)
    {
        long[] ordered = lbas.Distinct().OrderBy(value => value).ToArray();
        var result = new List<(long Start, long Count)>();
        if (ordered.Length == 0)
            return result;

        long start = ordered[0];
        long previous = ordered[0];
        for (int i = 1; i < ordered.Length; i++)
        {
            long current = ordered[i];
            if (current == previous + 1)
            {
                previous = current;
                continue;
            }

            result.Add((start, previous - start + 1));
            start = previous = current;
        }
        result.Add((start, previous - start + 1));
        return result;
    }

    private static int GetPayloadCapacity(DicSectorLayout layout)
    {
        if (layout.IsAudio || layout.IsUnknown || layout.Mode == 0)
            return 0;
        return layout.Mode == 2 && layout.Form == 2 ? 2324 : CookedSectorSize;
    }

    private static long CountPhysicalSectors(
        long startLba,
        long dataLength,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        int defaultMode,
        long sectorCount)
    {
        long remaining = dataLength;
        long count = 0;
        long lba = startLba;
        while (remaining > 0 && lba < sectorCount)
        {
            DicSectorLayout layout = GetLayout(layouts, lba, defaultMode);
            int capacity = GetPayloadCapacity(layout);
            if (capacity <= 0)
                return 0;
            remaining -= Math.Min(remaining, capacity);
            count++;
            lba++;
        }
        return remaining == 0 ? count : 0;
    }

    private static void BuildSyntheticCookedSkeleton(
        string outputPath,
        long sectorCount,
        IReadOnlyDictionary<long, byte[]> metadata,
        IProgress<DicImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        string partial = outputPath + ".partial";
        TryDelete(partial);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        const int sectorsPerBlock = 2048;
        byte[] block = new byte[sectorsPerBlock * CookedSectorSize];

        try
        {
            using (var output = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4 * 1024 * 1024, FileOptions.SequentialScan))
            {
                for (long blockStart = 0; blockStart < sectorCount; blockStart += sectorsPerBlock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sectorsThisBlock = (int)Math.Min((long)sectorsPerBlock, sectorCount - blockStart);
                    int bytesThisBlock = sectorsThisBlock * CookedSectorSize;
                    block.AsSpan(0, bytesThisBlock).Clear();

                    for (int local = 0; local < sectorsThisBlock; local++)
                    {
                        long lba = blockStart + local;
                        if (metadata.TryGetValue(lba, out byte[]? payload) && payload.Length == CookedSectorSize)
                            payload.AsSpan().CopyTo(block.AsSpan(local * CookedSectorSize, CookedSectorSize));
                    }

                    output.Write(block, 0, bytesThisBlock);
                    progress?.Report(new DicImportProgress(
                        "Skeleton", Math.Min(blockStart + sectorsThisBlock, sectorCount), sectorCount,
                        "Building cooked 2048-byte DIC skeleton"));
                }
            }

            TryDelete(outputPath);
            File.Move(partial, outputPath);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void BuildSyntheticSkeleton(
        string outputPath,
        long sectorCount,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        int defaultMode,
        IReadOnlyDictionary<long, byte[]> metadata,
        IReadOnlySet<long> dicMode2Form1QFaultLbas,
        IReadOnlySet<long> dicFill55ExceptHeaderLbas,
        IReadOnlySet<long> dicExactZeroSectorLbas,
        IReadOnlyDictionary<long, byte[]> dicExactRawSectorOverrides,
        IProgress<DicImportProgress>? progress,
        CancellationToken cancellationToken,
        out int mode1Count,
        out int mode2Form1Count,
        out int mode2Form2Count)
    {
        string partial = outputPath + ".partial";
        TryDelete(partial);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        const int sectorsPerBlock = 1024;
        int blockSize = sectorsPerBlock * RawSectorSize;
        byte[] block = new byte[blockSize];
        byte[] zero2048 = new byte[CookedSectorSize];
        byte[] zero2324 = new byte[2324];
        int mode1 = 0;
        int form1 = 0;
        int form2 = 0;

        try
        {
            // Keep the output handle in its own scope. Windows will not rename a
            // FileShare.None file while that handle is still open.
            using (var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4 * 1024 * 1024,
                FileOptions.SequentialScan))
            {
                for (long blockStart = 0; blockStart < sectorCount; blockStart += sectorsPerBlock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sectorsThisBlock = (int)Math.Min((long)sectorsPerBlock, sectorCount - blockStart);
                    int bytesThisBlock = sectorsThisBlock * RawSectorSize;

                    Parallel.For(
                        0,
                        sectorsThisBlock,
                        new ParallelOptions
                        {
                            CancellationToken = cancellationToken,
                            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                        },
                        local =>
                        {
                            long lba = blockStart + local;
                            DicSectorLayout layout = GetLayout(layouts, lba, defaultMode);
                            Span<byte> sector = block.AsSpan(local * RawSectorSize, RawSectorSize);

                            if (layout.Mode == 0)
                            {
                                SkeletonResurrectionService.BuildMode0Sector(
                                    lba,
                                    sector,
                                    layout.RawHeaderOverride);
                            }
                            else if (layout.IsAudio || layout.IsUnknown)
                            {
                                // EccEdc can map audio and malformed/unknown sectors. Their
                                // 2352-byte content is opaque and cannot be invented from ISO
                                // payloads, so leave a zero placeholder for an exact raw donor.
                                sector.Clear();
                            }
                            else if (layout.Mode == 1)
                            {
                                ReadOnlySpan<byte> payload = metadata.TryGetValue(lba, out byte[]? data) ? data : zero2048;
                                SkeletonResurrectionService.BuildMode1Sector(
                                    lba,
                                    payload,
                                    sector,
                                    layout.RawHeaderOverride);
                                Interlocked.Increment(ref mode1);
                            }
                            else if (layout.Mode == 2 && layout.Form == 2)
                            {
                                SkeletonResurrectionService.BuildMode2Form2Sector(
                                    lba,
                                    zero2324,
                                    layout.FileNumber,
                                    layout.ChannelNumber,
                                    layout.Submode,
                                    layout.CodingInfo,
                                    sector,
                                    generateEdc: layout.HasEdc,
                                    rawHeaderOverride: layout.RawHeaderOverride,
                                    xaSubheaderOverride: layout.XaSubheaderOverride);
                                Interlocked.Increment(ref form2);
                            }
                            else if (layout.Mode == 2)
                            {
                                ReadOnlySpan<byte> payload = metadata.TryGetValue(lba, out byte[]? data) ? data : zero2048;
                                SkeletonResurrectionService.BuildMode2Form1Sector(
                                    lba,
                                    payload,
                                    layout.FileNumber,
                                    layout.ChannelNumber,
                                    layout.Submode,
                                    layout.CodingInfo,
                                    sector,
                                    dicLoggedMode2Form1EccError: dicMode2Form1QFaultLbas.Contains(lba),
                                    rawHeaderOverride: layout.RawHeaderOverride,
                                    xaSubheaderOverride: layout.XaSubheaderOverride);
                                Interlocked.Increment(ref form1);
                            }
                            else
                            {
                                sector.Clear();
                            }

                            if (dicExactRawSectorOverrides.TryGetValue(lba, out byte[]? exactRaw) && exactRaw.Length == RawSectorSize)
                                exactRaw.AsSpan().CopyTo(sector);
                            else if (dicExactZeroSectorLbas.Contains(lba))
                                sector.Clear();
                            else if (dicFill55ExceptHeaderLbas.Contains(lba))
                                sector.Slice(16, RawSectorSize - 16).Fill(0x55);
                        });

                    output.Write(block, 0, bytesThisBlock);
                    long completed = blockStart + sectorsThisBlock;
                    progress?.Report(new DicImportProgress("Building", completed, sectorCount, $"Building synthetic skeleton — LBA {completed - 1:N0}"));
                }

                // Flush to disk while the handle is valid, then leave this scope so
                // it is fully disposed before the final rename below.
                output.Flush(true);
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(partial, outputPath);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        mode1Count = mode1;
        mode2Form1Count = form1;
        mode2Form2Count = form2;
    }

    private static long ResolveSectorCount(
        DicVolumeInfo volume,
        DicDiscInfo disc,
        IReadOnlyDictionary<long, DicSectorLayout> layouts)
    {
        long count = 0;
        if (disc.TrackEndLba is long end && end >= 0)
            count = Math.Max(count, end + 1);
        if (disc.TrackSectorCount is long trackCount)
            count = Math.Max(count, trackCount);
        foreach (DicDiscTrackInfo track in disc.Tracks)
        {
            if (track.EndLba is long trackEnd && trackEnd >= 0)
                count = Math.Max(count, trackEnd + 1);
        }
        if (volume.VolumeSpaceSize > 0)
            count = Math.Max(count, volume.VolumeSpaceSize);
        if (layouts.Count > 0)
            count = Math.Max(count, layouts.Keys.Max() + 1);
        if (disc.ImageSize is long imageSize && imageSize > 0 && imageSize % RawSectorSize == 0)
            count = Math.Max(count, imageSize / RawSectorSize);
        return count;
    }

    private static int ApplyDiscTrackFallbacksAndFillInference(
        DicDiscInfo disc,
        Dictionary<long, DicSectorLayout> layouts,
        long sectorCount,
        int defaultMode)
    {
        int added = 0;

        // Per-sector "2336 bytes ... 0x55" records deliberately omit the mode
        // because the body has already been replaced.  The disc track map still
        // tells us whether the untouched 16-byte header is Mode 1 or Mode 2.
        // Use that explicit track evidence to make the fill sector fully
        // synthesizable; if no track range exists, keep IsUnknown=true so an exact
        // raw donor remains preferred for the header byte.
        foreach (long lba in layouts
                     .Where(pair => pair.Value.HasExplicitFill55 && pair.Value.IsUnknown)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            DicSectorLayout? baseline = GetDiscTrackBaselineLayout(disc, lba, defaultMode);
            if (baseline is null || baseline.IsAudio || baseline.Mode is not (1 or 2))
                continue;

            DicSectorLayout current = layouts[lba];
            if (baseline.Mode == 2)
            {
                byte[] fillXa = Enumerable.Repeat((byte)0x55, 8).ToArray();
                layouts[lba] = current with
                {
                    Mode = 2,
                    Form = 1,
                    FileNumber = 0x55,
                    ChannelNumber = 0x55,
                    Submode = 0x55,
                    CodingInfo = 0x55,
                    HasEdc = true,
                    XaSubheaderOverride = fillXa,
                    IsUnknown = false,
                    IsDiscTrackFallback = true
                };
            }
            else
            {
                layouts[lba] = current with
                {
                    Mode = 1,
                    Form = 1,
                    IsUnknown = false,
                    IsDiscTrackFallback = true
                };
            }
        }

        // Fill only sectors not covered by reliable EccEdc records.  This is a
        // classification fallback, not byte-level proof: audio remains opaque and
        // data sectors get the logged track mode with conventional framing.
        for (long lba = 0; lba < sectorCount; lba++)
        {
            if (layouts.ContainsKey(lba))
                continue;

            DicSectorLayout? baseline = GetDiscTrackBaselineLayout(disc, lba, defaultMode);
            if (baseline is null)
                continue;

            layouts[lba] = baseline;
            added++;
        }

        return added;
    }

    private static DicSectorLayout? GetDiscTrackBaselineLayout(DicDiscInfo disc, long lba, int defaultMode)
    {
        // Audio INDEX 00 belongs to the following audio track even though DIC's
        // high-level "Data Track" length can extend through that pregap.
        foreach (DicDiscTrackInfo track in disc.Tracks.Where(track => track.IsAudio))
        {
            long? start = track.Index0Lba is long index0 &&
                          track.Index1Lba is long index1 &&
                          index0 >= 0 && index0 < index1
                ? index0
                : track.StartLba;
            if (start is long audioStart && track.EndLba is long audioEnd && lba >= audioStart && lba <= audioEnd)
            {
                return new DicSectorLayout(
                    -1, 1, 0, 0, 0, 0,
                    ReportedLba: lba,
                    IsAudio: true,
                    IsDiscTrackFallback: true);
            }
        }

        foreach (DicDiscTrackInfo track in disc.Tracks.Where(track => !track.IsAudio))
        {
            long? start = track.DataStartLba ?? track.StartLba;
            long? end = track.DataEndLba ?? track.EndLba;
            if (start is not long dataStart || end is not long dataEnd || lba < dataStart || lba > dataEnd)
                continue;

            int mode = track.Mode is int loggedMode && loggedMode is 0 or 1 or 2
                ? loggedMode
                : defaultMode;
            return mode switch
            {
                0 => new DicSectorLayout(0, 1, 0, 0, 0, 0, ReportedLba: lba, IsDiscTrackFallback: true),
                2 => new DicSectorLayout(2, 1, 0, 0, 0x08, 0, HasEdc: true, ReportedLba: lba, IsDiscTrackFallback: true),
                _ => new DicSectorLayout(1, 1, 0, 0, 0, 0, HasEdc: true, ReportedLba: lba, IsDiscTrackFallback: true)
            };
        }

        return null;
    }

    private static DicSectorLayout GetLayout(IReadOnlyDictionary<long, DicSectorLayout> layouts, long lba, int defaultMode)
    {
        if (layouts.TryGetValue(lba, out DicSectorLayout? layout) && layout is not null)
            return layout;
        return defaultMode == 2
            ? new DicSectorLayout(2, 1, 0, 0, 0x08, 0, HasEdc: true)
            : new DicSectorLayout(1, 1, 0, 0, 0, 0, HasEdc: true);
    }

    private static byte ParseHexByte(Match match, byte fallback = 0)
    {
        return match.Success && byte.TryParse(match.Groups["v"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
            ? value
            : fallback;
    }

    private static bool TryValue(string line, string label, out string value)
    {
        int index = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            value = string.Empty;
            return false;
        }
        value = line[(index + label.Length)..].Trim();
        return true;
    }

    private static bool TryLongValue(string line, string label, out long value)
    {
        value = 0;
        if (!TryValue(line, label, out string text))
            return false;
        Match number = Regex.Match(text, @"^-?\d+");
        return number.Success && long.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsCurrentDirectoryIdentifier(string identifier)
        => identifier.Length == 1 && identifier[0] == '\0';

    private static bool IsSpecialDirectoryIdentifier(string identifier)
        => identifier.Length == 1 && (identifier[0] == '\0' || identifier[0] == '\x01');

    private static string NormalizeIsoPath(string path)
    {
        string value = path.Replace('\\', '/').Trim();
        value = Regex.Replace(value, @";\d+(?=/|$)", string.Empty);
        if (!value.StartsWith('/')) value = "/" + value;
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        return value;
    }

    private static int PathQuality(string path)
    {
        int score = path.Length;
        if (!path.Contains('~')) score += 10000;
        if (path.Any(char.IsLower)) score += 1000;
        return score;
    }

    private static string StripKnownSuffix(string fileName)
    {
        string[] suffixes =
        {
            ".img_EccEdc.txt",
            ".scm_EccEdc.txt",
            "_EccEdc.txt",
            "_volDesc.txt",
            "_disc.txt",
            "_mainInfo.txt",
            "_mainError.txt"
        };
        foreach (string suffix in suffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? FindCompanion(string directory, string expectedName)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase));
    }

    private static long DivideRoundUp(long value, long divisor) => value <= 0 ? 0 : (value + divisor - 1) / divisor;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup must not hide the original import failure.
        }
    }

}
