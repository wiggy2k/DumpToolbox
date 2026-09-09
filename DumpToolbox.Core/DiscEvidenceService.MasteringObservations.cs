using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class DiscEvidenceService
{
    private const int LogicalSectorSize = 2048;
    private static readonly byte[] CeQuadratLinkSignature = Encoding.ASCII.GetBytes("CeQuadrat Joliet directory link table");

    private sealed record DiscMasteringObservation(
        string Kind,
        string Region,
        long StartLba,
        long EndLba,
        int SectorCount,
        int NonZeroSectorCount,
        long NonZeroBytes,
        int? FirstNonZeroOffset,
        string PayloadSha1,
        long? DuplicateLba,
        string Details);

    private readonly record struct DiscRecordGeometryFlags(bool OutsideVolume, bool OverlapsMetadata, bool OverlapsFile);

    private static async Task<List<DiscMasteringObservation>> InspectMasteringObservationsAsync(
        SectorReader reader,
        IReadOnlyList<DiscVolumeDescriptorEvidence> descriptors,
        IReadOnlyList<DiscFilesystemRecordEvidence> iso,
        IReadOnlyList<DiscFilesystemRecordEvidence> joliet,
        CancellationToken cancellationToken)
    {
        var result = new List<DiscMasteringObservation>();
        DiscVolumeDescriptorEvidence? primary = descriptors.FirstOrDefault(item => item.DescriptorType == 1);
        if (primary is null)
            return result;

        long totalSectors = reader.LogicalSectorCount;
        long volumeSectors = Math.Min(primary.VolumeSpaceSize, totalSectors);
        var occupied = new List<(long Start, long End)>();
        foreach (DiscVolumeDescriptorEvidence descriptor in descriptors)
        {
            AddRange(occupied, descriptor.DescriptorLba, 1, volumeSectors);
            AddRange(occupied, descriptor.RootExtent, SectorCount(descriptor.RootLength), volumeSectors);
            int pathSectors = SectorCount(descriptor.PathTableSize);
            foreach (uint lba in new[]
                     {
                         descriptor.TypeLPathTableLba, descriptor.OptionalTypeLPathTableLba,
                         descriptor.TypeMPathTableLba, descriptor.OptionalTypeMPathTableLba
                     }.Where(value => value != 0).Distinct())
                AddRange(occupied, lba, pathSectors, volumeSectors);
        }
        foreach (DiscFilesystemRecordEvidence record in iso.Concat(joliet))
            AddRange(occupied, record.Extent, SectorCount(record.Length), volumeSectors);

        DiscVolumeDescriptorEvidence? terminator = descriptors.FirstOrDefault(item => item.DescriptorType == 255);
        if (terminator is not null && primary.TypeLPathTableLba > terminator.DescriptorLba + 1 &&
            primary.TypeLPathTableLba - (terminator.DescriptorLba + 1) <= 64)
        {
            long start = terminator.DescriptorLba + 1;
            long end = Math.Min(primary.TypeLPathTableLba, volumeSectors);
            byte[] bytes = await ReadLogicalRangeAsync(reader, start, end, cancellationToken).ConfigureAwait(false);
            if (bytes.AsSpan().StartsWith(CeQuadratLinkSignature) && bytes.Length >= 48)
            {
                uint pairCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44, 4));
                long requiredBytes = 48L + pairCount * 8L;
                int usedBytes = checked((int)Math.Min(requiredBytes, bytes.Length));
                byte[] unused = bytes[usedBytes..];
                int dirtyUnused = CountNonZero(unused);
                result.Add(CreateObservation(
                    "CEQUADRAT_JOLIET_LINK_TABLE", "UNCLAIMED", start, end, bytes,
                    $"pairs={pairCount};required_bytes={requiredBytes};available_bytes={bytes.Length};unused_nonzero_bytes={dirtyUnused}"));
                if (dirtyUnused > 0)
                {
                    result.Add(CreateObservation(
                        "CEQUADRAT_LINK_UNUSED_TAIL", "UNCLAIMED", start + usedBytes / LogicalSectorSize, end,
                        unused, $"pair_data_ends_at_byte={usedBytes};candidate_only=true"));
                }
                AddRange(occupied, start, end - start, volumeSectors);
            }
        }

        foreach (long lba in new[] { volumeSectors - 2, volumeSectors - 1 }.Where(value => value >= 0).Distinct())
        {
            byte[] payload = await reader.ReadAsync(lba, cancellationToken).ConfigureAwait(false);
            if (CeQuadratFooterCodec.IsExactTextPayload(payload, lba))
            {
                result.Add(CreateObservation("CEQUADRAT_TEXT_FORMATTER", "UNCLAIMED", lba, lba + 1, payload,
                    $"relative_to_volume_end={lba - volumeSectors};variant=deterministic_text;template_exact=true;lba_fields_valid=true;marker_valid=true;zero_fill_valid=true"));
                AddRange(occupied, lba, 1, volumeSectors);
            }
            else if (payload.AsSpan().StartsWith(CeQuadratFooterCodec.TextSignature))
            {
                result.Add(CreateObservation("CEQUADRAT_TEXT_FORMATTER_CANDIDATE", "UNCLAIMED", lba, lba + 1, payload,
                    $"relative_to_volume_end={lba - volumeSectors};template_exact=false;candidate_only=true"));
                AddRange(occupied, lba, 1, volumeSectors);
            }
            else if (CeQuadratFooterCodec.TryClassifyExactBinaryPayload(payload, lba, out CeQuadratBinaryFooterVariant variant))
            {
                bool masteringIdentityMatches = IsCeQuadratOrWinOnCdPreparer(primary.DataPreparerId);
                string kind = masteringIdentityMatches ? "CEQUADRAT_BINARY_FOOTER" : "BINARY_VOLUME_FOOTER_CANDIDATE";
                result.Add(CreateObservation(kind, "UNCLAIMED", lba, lba + 1, payload,
                    $"relative_to_volume_end={lba - volumeSectors};variant={variant.ToString().ToLowerInvariant()};template_exact=true;checksum_valid=true;self_lba_valid=true;zero_tail_valid=true" +
                    (masteringIdentityMatches ? string.Empty : ";candidate_only=true")));
                AddRange(occupied, lba, 1, volumeSectors);
            }
        }

        await InspectDescriptorReservedBytesAsync(reader, descriptors, result, cancellationToken).ConfigureAwait(false);
        await InspectRegionAsync(reader, "SYSTEM_AREA", 0, Math.Min(16, totalSectors), result, cancellationToken).ConfigureAwait(false);

        foreach ((long start, long end) in Complement(MergeRanges(occupied), 16, volumeSectors))
            await InspectRegionAsync(reader, "UNCLAIMED", start, end, result, cancellationToken).ConfigureAwait(false);

        int postStartIndex = result.Count;
        await InspectRegionAsync(reader, "POST_VOLUME", volumeSectors, totalSectors, result, cancellationToken).ConfigureAwait(false);
        await LocatePostVolumeDuplicatesAsync(reader, volumeSectors, result, postStartIndex, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task InspectDescriptorReservedBytesAsync(
        SectorReader reader,
        IReadOnlyList<DiscVolumeDescriptorEvidence> descriptors,
        List<DiscMasteringObservation> result,
        CancellationToken cancellationToken)
    {
        foreach (DiscVolumeDescriptorEvidence descriptor in descriptors.Where(item => item.DescriptorType is 1 or 2))
        {
            byte[] payload = await reader.ReadAsync(descriptor.DescriptorLba, cancellationToken).ConfigureAwait(false);
            foreach ((int start, int end, string field, string kind) in new[]
                     {
                         (7, 8, "byte_7", "NONZERO_DESCRIPTOR_RESERVED_BYTES"),
                         (73, 80, "reserved_73_79", "NONZERO_DESCRIPTOR_RESERVED_BYTES"),
                         (882, 1395, "application_use_882_1394", "NONZERO_DESCRIPTOR_APPLICATION_USE"),
                         (1395, 2048, "reserved_1395_2047", "NONZERO_DESCRIPTOR_RESERVED_BYTES")
                     })
            {
                byte[] bytes = payload[start..end];
                if (CountNonZero(bytes) == 0)
                    continue;
                result.Add(CreateObservation(kind, "DESCRIPTOR",
                    descriptor.DescriptorLba, descriptor.DescriptorLba + 1, bytes,
                    $"namespace={descriptor.Namespace};field={field};payload_offset={start};candidate_only=true"));
            }
        }
    }

    private static async Task InspectRegionAsync(
        SectorReader reader,
        string region,
        long start,
        long end,
        List<DiscMasteringObservation> result,
        CancellationToken cancellationToken)
    {
        if (start >= end)
            return;
        int nonZeroSectors = 0;
        long nonZeroBytes = 0;
        for (long lba = start; lba < end; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = await reader.ReadAsync(lba, cancellationToken).ConfigureAwait(false);
            int count = CountNonZero(payload);
            if (count == 0)
                continue;
            nonZeroSectors++;
            nonZeroBytes += count;
            string kind = ClassifyUnexpectedSector(payload, region, lba);
            result.Add(CreateObservation(kind, region, lba, lba + 1, payload, "candidate_only=true"));
        }
        result.Add(new DiscMasteringObservation(
            "REGION_SUMMARY", region, start, end, checked((int)Math.Min(int.MaxValue, end - start)),
            nonZeroSectors, nonZeroBytes, null, string.Empty, null, string.Empty));
    }

    private static async Task LocatePostVolumeDuplicatesAsync(
        SectorReader reader,
        long volumeSectors,
        List<DiscMasteringObservation> observations,
        int firstObservationIndex,
        CancellationToken cancellationToken)
    {
        int[] targets = Enumerable.Range(firstObservationIndex, observations.Count - firstObservationIndex)
            .Where(index => observations[index].Region == "POST_VOLUME" && observations[index].Kind != "REGION_SUMMARY")
            .Take(8)
            .ToArray();
        if (targets.Length == 0)
            return;

        var targetBytes = new Dictionary<int, byte[]>();
        foreach (int index in targets)
            targetBytes[index] = await reader.ReadAsync(observations[index].StartLba, cancellationToken).ConfigureAwait(false);
        var unresolved = targets.ToHashSet();
        for (long lba = 0; lba < volumeSectors && unresolved.Count > 0; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = await reader.ReadAsync(lba, cancellationToken).ConfigureAwait(false);
            foreach (int index in unresolved.ToArray())
            {
                if (!payload.AsSpan().SequenceEqual(targetBytes[index]))
                    continue;
                observations[index] = observations[index] with { DuplicateLba = lba };
                unresolved.Remove(index);
            }
        }
    }

    private static Dictionary<(string Namespace, uint DirectoryExtent, int RecordOffset, int RecordIndex), DiscRecordGeometryFlags>
        AnalyseRecordGeometry(ImageEvidence evidence)
    {
        DiscFilesystemRecordEvidence[] records = evidence.Iso.Concat(evidence.JolietRecords).ToArray();
        long volumeSectors = evidence.Primary?.VolumeSpaceSize ?? 0;
        var metadata = new List<(long Start, long End)>();
        foreach (DiscVolumeDescriptorEvidence descriptor in evidence.Descriptors)
        {
            AddRange(metadata, descriptor.DescriptorLba, 1, volumeSectors);
            AddRange(metadata, descriptor.RootExtent, SectorCount(descriptor.RootLength), volumeSectors);
            int pathSectors = SectorCount(descriptor.PathTableSize);
            foreach (uint lba in new[]
                     {
                         descriptor.TypeLPathTableLba, descriptor.OptionalTypeLPathTableLba,
                         descriptor.TypeMPathTableLba, descriptor.OptionalTypeMPathTableLba
                     }.Where(value => value != 0).Distinct())
                AddRange(metadata, lba, pathSectors, volumeSectors);
        }
        foreach (DiscFilesystemRecordEvidence directory in records.Where(record => record.IsDirectory))
            AddRange(metadata, directory.Extent, SectorCount(directory.Length), volumeSectors);
        List<(long Start, long End)> mergedMetadata = MergeRanges(metadata);

        var overlapsFile = new HashSet<(string Namespace, uint DirectoryExtent, int RecordOffset, int RecordIndex)>();
        foreach (IGrouping<string, DiscFilesystemRecordEvidence> namespaceGroup in records
                     .Where(record => !record.IsDirectory && record.Length > 0)
                     .GroupBy(record => record.Namespace))
        {
            var groups = namespaceGroup
                .GroupBy(record => (Start: (long)record.Extent, End: (long)record.Extent + SectorCount(record.Length)))
                .Select(group => new { group.Key.Start, group.Key.End, Records = group.ToArray() })
                .OrderBy(group => group.Start).ThenByDescending(group => group.End).ToArray();
            long maximumEnd = -1;
            DiscFilesystemRecordEvidence[]? maximumRecords = null;
            foreach (var group in groups)
            {
                if (group.Start < maximumEnd && maximumRecords is not null)
                {
                    foreach (DiscFilesystemRecordEvidence record in group.Records)
                        overlapsFile.Add(GeometryRecordKey(record));
                    foreach (DiscFilesystemRecordEvidence record in maximumRecords)
                        overlapsFile.Add(GeometryRecordKey(record));
                }
                if (group.End > maximumEnd)
                {
                    maximumEnd = group.End;
                    maximumRecords = group.Records;
                }
            }
        }

        var result = new Dictionary<(string, uint, int, int), DiscRecordGeometryFlags>();
        foreach (DiscFilesystemRecordEvidence record in records)
        {
            long end = (long)record.Extent + SectorCount(record.Length);
            bool outside = volumeSectors > 0 && end > volumeSectors;
            bool overlapsMetadata = !record.IsDirectory && record.Length > 0 &&
                                    mergedMetadata.Any(range => record.Extent < range.End && end > range.Start);
            result[GeometryRecordKey(record)] = new DiscRecordGeometryFlags(
                outside, overlapsMetadata, overlapsFile.Contains(GeometryRecordKey(record)));
        }
        return result;
    }

    private static (string Namespace, uint DirectoryExtent, int RecordOffset, int RecordIndex) GeometryRecordKey(
        DiscFilesystemRecordEvidence record)
        => (record.Namespace, record.DirectoryExtent, record.RecordOffset, record.RecordIndex);

    private static string ClassifyUnexpectedSector(ReadOnlySpan<byte> payload, string region, long lba)
    {
        if (payload.StartsWith(CeQuadratLinkSignature))
            return "CEQUADRAT_JOLIET_LINK_TABLE";
        if (CeQuadratFooterCodec.IsExactTextPayload(payload, lba))
            return "CEQUADRAT_TEXT_FORMATTER";
        if (payload.StartsWith(CeQuadratFooterCodec.TextSignature))
            return "CEQUADRAT_TEXT_FORMATTER_CANDIDATE";
        return region switch
        {
            "SYSTEM_AREA" => "NONZERO_SYSTEM_AREA",
            "POST_VOLUME" => "NONZERO_POST_VOLUME",
            _ => "NONZERO_UNCLAIMED_SECTOR"
        };
    }

    private static bool IsCeQuadratOrWinOnCdPreparer(string value)
        => value.Contains("CEQUADRAT", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("CEQUDRAT", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("WINONCD", StringComparison.OrdinalIgnoreCase);

    private static DiscMasteringObservation CreateObservation(
        string kind, string region, long start, long end, byte[] bytes, string details)
    {
        int first = Array.FindIndex(bytes, value => value != 0);
        int nonZeroSectors = 0;
        for (int offset = 0; offset < bytes.Length; offset += LogicalSectorSize)
        {
            int length = Math.Min(LogicalSectorSize, bytes.Length - offset);
            if (CountNonZero(bytes.AsSpan(offset, length)) > 0)
                nonZeroSectors++;
        }
        return new DiscMasteringObservation(kind, region, start, end,
            checked((int)Math.Max(1, end - start)), nonZeroSectors,
            CountNonZero(bytes), first < 0 ? null : first,
            Convert.ToHexString(SHA1.HashData(bytes)), null, details);
    }

    private static int CountNonZero(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        foreach (byte value in bytes)
            if (value != 0)
                count++;
        return count;
    }

    private static int SectorCount(uint length)
        => length == 0 ? 0 : checked((int)(((ulong)length + LogicalSectorSize - 1) / LogicalSectorSize));

    private static async Task<byte[]> ReadLogicalRangeAsync(
        SectorReader reader, long start, long end, CancellationToken cancellationToken)
    {
        byte[] result = new byte[checked((int)((end - start) * LogicalSectorSize))];
        for (long lba = start; lba < end; lba++)
        {
            byte[] payload = await reader.ReadAsync(lba, cancellationToken).ConfigureAwait(false);
            payload.CopyTo(result, checked((int)((lba - start) * LogicalSectorSize)));
        }
        return result;
    }

    private static void AddRange(List<(long Start, long End)> ranges, long start, long count, long limit)
    {
        if (count <= 0 || start < 0 || start >= limit)
            return;
        ranges.Add((start, Math.Min(limit, checked(start + count))));
    }

    private static List<(long Start, long End)> MergeRanges(IEnumerable<(long Start, long End)> source)
    {
        var result = new List<(long Start, long End)>();
        foreach ((long start, long end) in source.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (result.Count == 0 || start > result[^1].End)
                result.Add((start, end));
            else if (end > result[^1].End)
                result[^1] = (result[^1].Start, end);
        }
        return result;
    }

    private static IEnumerable<(long Start, long End)> Complement(
        IReadOnlyList<(long Start, long End)> occupied, long start, long end)
    {
        long cursor = start;
        foreach ((long rangeStart, long rangeEnd) in occupied)
        {
            if (rangeEnd <= start)
                continue;
            if (rangeStart >= end)
                break;
            if (cursor < rangeStart)
                yield return (cursor, Math.Min(rangeStart, end));
            cursor = Math.Max(cursor, rangeEnd);
        }
        if (cursor < end)
            yield return (cursor, end);
    }
}
