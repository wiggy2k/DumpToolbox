using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

internal sealed record UdfDescriptorStructureEvidence(
    string Sequence,
    int SequenceIndex,
    long Lba,
    ushort TagId,
    ushort TagVersion,
    byte TagChecksum,
    bool TagChecksumValid,
    ushort TagSerial,
    ushort DescriptorCrc,
    ushort DescriptorCrcLength,
    bool DescriptorCrcValid,
    uint TagLocation,
    int ReservedNonZeroBytes,
    string ImplementationIdentifier,
    byte[] Payload,
    string PayloadSha1,
    string Details);

internal sealed record UdfPartitionMapStructureEvidence(
    long LogicalVolumeDescriptorLba,
    int MapIndex,
    byte MapType,
    int MapLength,
    string Identifier,
    ushort VolumeSequenceNumber,
    ushort PartitionNumber,
    int ReservedNonZeroBytes,
    byte[] Payload,
    string PayloadSha1);

internal sealed record UdfVatStructureEvidence(
    int Generation,
    bool IsLatest,
    long Lba,
    long PartitionBlock,
    ushort TagId,
    byte FileType,
    ushort TagSerial,
    bool TagChecksumValid,
    bool DescriptorCrcValid,
    int AllocationType,
    long InformationLength,
    int HeaderLength,
    int ImplementationUseLength,
    string ImplementationIdentifier,
    byte[] ImplementationUse,
    uint PreviousVatIcbLocation,
    uint FileCount,
    uint DirectoryCount,
    ushort MinimumReadRevision,
    ushort MinimumWriteRevision,
    ushort MaximumWriteRevision,
    int EntryCount,
    int MappedEntryCount,
    int UnusedEntryCount,
    int OutOfRangeEntryCount,
    int ReservedNonZeroBytes,
    byte[] Payload,
    string PayloadSha1);

internal sealed record UdfStructureEvidence(
    IReadOnlyList<UdfDescriptorStructureEvidence> Descriptors,
    IReadOnlyList<UdfPartitionMapStructureEvidence> PartitionMaps,
    IReadOnlyList<UdfVatStructureEvidence> Vats)
{
    public static UdfStructureEvidence Empty { get; } = new([], [], []);
}

/// <summary>
/// Captures UDF structures as mastering evidence. This intentionally keeps raw
/// structure hashes and required-zero violations separate from logical file hashes.
/// </summary>
internal static class UdfStructureInspector
{
    private const int SectorSize = SkeletonResurrectionService.CookedSectorSize;

    public static UdfStructureEvidence Inspect(string imagePath, CancellationToken cancellationToken)
    {
        using UdfImageReader.OpticalPayloadStream payload = UdfImageReader.OpticalPayloadStream.Open(imagePath);
        return Inspect(payload, cancellationToken);
    }

    internal static UdfStructureEvidence Inspect(
        UdfImageReader.OpticalPayloadStream payload,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<UdfDescriptorStructureEvidence>();
        var partitionMaps = new List<UdfPartitionMapStructureEvidence>();
        var partitions = new Dictionary<ushort, (uint Start, uint Length)>();
        var anchors = FindAnchors(payload);
        var seenSequences = new HashSet<(string Sequence, uint Start, uint Length)>();
        var seenDescriptors = new HashSet<(string Sequence, long Lba)>();

        foreach ((long lba, byte[] bytes) in anchors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptors.Add(BuildDescriptor("anchor", descriptors.Count(item => item.Sequence == "anchor"), lba, bytes,
                CountNonZero(bytes, 32, 480), string.Empty,
                $"main={BitConverter.ToUInt32(bytes, 20)}+{BitConverter.ToUInt32(bytes, 16)};" +
                $"reserve={BitConverter.ToUInt32(bytes, 28)}+{BitConverter.ToUInt32(bytes, 24)}"));

            ReadSequence("main", BitConverter.ToUInt32(bytes, 20), BitConverter.ToUInt32(bytes, 16));
            ReadSequence("reserve", BitConverter.ToUInt32(bytes, 28), BitConverter.ToUInt32(bytes, 24));
        }

        UdfPartitionMapStructureEvidence? virtualMap = partitionMaps.FirstOrDefault(map =>
            map.Identifier.Equals("*UDF Virtual Partition", StringComparison.Ordinal));
        if (virtualMap is not null && partitions.TryGetValue(virtualMap.PartitionNumber, out var partition))
        {
            payload.PhysicalPartitionStart = partition.Start;
            IReadOnlyList<UdfVatStructureEvidence> vats = ReadVatChain(payload, partition.Start, partition.Length, cancellationToken);
            return new(descriptors, partitionMaps, vats);
        }

        return new(descriptors, partitionMaps, []);

        void ReadSequence(string name, uint start, uint length)
        {
            if (length == 0 || !seenSequences.Add((name, start, length)))
                return;
            long count = Math.Min(256, (length + SectorSize - 1L) / SectorSize);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long lba = start + index;
                if (lba < 0 || lba >= payload.PhysicalSectorCount)
                    break;
                byte[] sector = new byte[SectorSize];
                payload.ReadPhysicalPayloadSector(lba, sector);
                ushort tag = BitConverter.ToUInt16(sector, 0);
                if (tag == 0)
                    break;

                int reservedNonZero = 0;
                string implementationIdentifier = string.Empty;
                string details = string.Empty;
                if (tag == 2)
                    reservedNonZero = CountNonZero(sector, 32, 480);
                else if (tag == 5)
                {
                    ushort number = BitConverter.ToUInt16(sector, 22);
                    uint partitionStart = BitConverter.ToUInt32(sector, 188);
                    uint partitionLength = BitConverter.ToUInt32(sector, 192);
                    partitions[number] = (partitionStart, partitionLength);
                    reservedNonZero = CountNonZero(sector, 356, 156);
                    details = $"partition={number};start={partitionStart};length={partitionLength};access={BitConverter.ToUInt32(sector, 184)}";
                }
                else if (tag == 6)
                {
                    uint mapLength = BitConverter.ToUInt32(sector, 264);
                    uint mapCount = BitConverter.ToUInt32(sector, 268);
                    implementationIdentifier = ReadEntityIdentifier(sector, 272);
                    details = $"map_length={mapLength};map_count={mapCount};logical_block_size={BitConverter.ToUInt32(sector, 212)}";
                    ReadPartitionMaps(lba, sector, mapLength, mapCount, partitionMaps);
                    int afterMaps = checked(440 + (int)Math.Min(mapLength, SectorSize - 440));
                    if (afterMaps < 512)
                        reservedNonZero = CountNonZero(sector, afterMaps, 512 - afterMaps);
                }
                else if (tag == 4)
                    implementationIdentifier = ReadEntityIdentifier(sector, 20);

                if (seenDescriptors.Add((name, lba)))
                    descriptors.Add(BuildDescriptor(name, index, lba, sector, reservedNonZero, implementationIdentifier, details));
                if (tag == 8)
                    break;
            }
        }
    }

    private static IReadOnlyList<(long Lba, byte[] Bytes)> FindAnchors(UdfImageReader.OpticalPayloadStream payload)
    {
        long last = payload.PhysicalSectorCount - 1;
        long[] candidates = { 256, last - 256, last, 512 };
        var anchors = new List<(long, byte[])>();
        var seen = new HashSet<long>();
        foreach (long lba in candidates)
        {
            if (lba < 0 || lba >= payload.PhysicalSectorCount || !seen.Add(lba))
                continue;
            byte[] sector = new byte[SectorSize];
            payload.ReadPhysicalPayloadSector(lba, sector);
            if (BitConverter.ToUInt16(sector, 0) == 2)
                anchors.Add((lba, sector));
        }
        return anchors;
    }

    private static void ReadPartitionMaps(
        long lvdLba,
        byte[] descriptor,
        uint declaredLength,
        uint declaredCount,
        ICollection<UdfPartitionMapStructureEvidence> destination)
    {
        int end = checked(440 + (int)Math.Min(declaredLength, descriptor.Length - 440));
        int position = 440;
        for (int index = 0; index < declaredCount && position + 2 <= end; index++)
        {
            byte type = descriptor[position];
            int length = descriptor[position + 1];
            if (length < 2 || position + length > end)
                break;
            ReadOnlySpan<byte> map = descriptor.AsSpan(position, length);
            string identifier = string.Empty;
            ushort volumeSequence = 0;
            ushort partitionNumber = 0;
            int reservedNonZero = 0;
            if (type == 1 && length >= 6)
            {
                volumeSequence = BitConverter.ToUInt16(map[2..4]);
                partitionNumber = BitConverter.ToUInt16(map[4..6]);
            }
            else if (type == 2 && length >= 64)
            {
                identifier = Encoding.ASCII.GetString(map[5..28]).TrimEnd('\0', ' ');
                volumeSequence = BitConverter.ToUInt16(map[36..38]);
                partitionNumber = BitConverter.ToUInt16(map[38..40]);
                reservedNonZero = CountNonZero(map, 2, 2) + CountNonZero(map, 40, 24);
            }
            destination.Add(new(
                lvdLba, index, type, length, identifier, volumeSequence, partitionNumber,
                reservedNonZero, map.ToArray(), Convert.ToHexString(SHA1.HashData(map))));
            position += length;
        }
    }

    private static IReadOnlyList<UdfVatStructureEvidence> ReadVatChain(
        UdfImageReader.OpticalPayloadStream payload,
        uint partitionStart,
        uint partitionLength,
        CancellationToken cancellationToken)
    {
        long latestLba = -1;
        byte[] latestEntry = Array.Empty<byte>();
        byte[] latestVat = Array.Empty<byte>();
        byte[] probe = new byte[SectorSize];
        for (long lba = payload.PhysicalSectorCount - 1; lba >= partitionStart; lba--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            payload.ReadPhysicalPayloadSector(lba, probe);
            ushort tag = BitConverter.ToUInt16(probe, 0);
            if (tag is not (261 or 266) || probe[27] is not (0 or 248))
                continue;
            if (payload.TryReadVatFileAt(lba, out latestEntry, out latestVat))
            {
                latestLba = lba;
                break;
            }
        }
        if (latestLba < 0)
            return [];

        var result = new List<UdfVatStructureEvidence>();
        var seen = new HashSet<long>();
        long currentLba = latestLba;
        byte[] currentEntry = latestEntry;
        byte[] currentVat = latestVat;
        for (int generation = 0; generation < 1024 && seen.Add(currentLba); generation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdfVatStructureEvidence item = BuildVat(
                generation, generation == 0, currentLba, partitionStart, partitionLength, currentEntry, currentVat);
            result.Add(item);
            if (item.PreviousVatIcbLocation == uint.MaxValue)
                break;
            currentLba = checked(partitionStart + item.PreviousVatIcbLocation);
            if (currentLba < partitionStart || currentLba >= payload.PhysicalSectorCount ||
                !payload.TryReadVatFileAt(currentLba, out currentEntry, out currentVat))
                break;
        }
        return result;
    }

    private static UdfVatStructureEvidence BuildVat(
        int generation,
        bool isLatest,
        long lba,
        uint partitionStart,
        uint partitionLength,
        byte[] fileEntry,
        byte[] vat)
    {
        UdfImageReader.OpticalPayloadStream.TryGetVatEntriesForFileType(
            fileEntry[27], vat, out int entriesOffset, out int entryCount);
        bool modern = entriesOffset >= 152;
        int implementationLength = modern ? BitConverter.ToUInt16(vat, 2) : 0;
        string implementationIdentifier = implementationLength >= 32 && vat.Length >= 176
            ? ReadEntityIdentifier(vat, 152)
            : string.Empty;
        byte[] implementationUse = modern && implementationLength > 0 && 152 + implementationLength <= vat.Length
            ? vat.AsSpan(152, implementationLength).ToArray()
            : [];
        uint previous = modern ? BitConverter.ToUInt32(vat, 132) : uint.MaxValue;
        int mapped = 0;
        int unused = 0;
        int outOfRange = 0;
        for (int index = 0; index < entryCount; index++)
        {
            uint value = BitConverter.ToUInt32(vat, entriesOffset + index * 4);
            if (value == uint.MaxValue)
                unused++;
            else
            {
                mapped++;
                if (value >= partitionLength)
                    outOfRange++;
            }
        }

        return new(
            generation,
            isLatest,
            lba,
            lba - partitionStart,
            BitConverter.ToUInt16(fileEntry, 0),
            fileEntry[27],
            BitConverter.ToUInt16(fileEntry, 6),
            HasValidTagChecksum(fileEntry),
            HasValidDescriptorCrc(fileEntry),
            BitConverter.ToUInt16(fileEntry, 34) & 7,
            vat.LongLength,
            modern ? BitConverter.ToUInt16(vat, 0) : 0,
            implementationLength,
            implementationIdentifier,
            implementationUse,
            previous,
            modern ? BitConverter.ToUInt32(vat, 136) : 0,
            modern ? BitConverter.ToUInt32(vat, 140) : 0,
            modern ? BitConverter.ToUInt16(vat, 144) : (ushort)0,
            modern ? BitConverter.ToUInt16(vat, 146) : (ushort)0,
            modern ? BitConverter.ToUInt16(vat, 148) : (ushort)0,
            entryCount,
            mapped,
            unused,
            outOfRange,
            modern ? CountNonZero(vat, 150, 2) : 0,
            vat.ToArray(),
            Convert.ToHexString(SHA1.HashData(vat)));
    }

    private static UdfDescriptorStructureEvidence BuildDescriptor(
        string sequence,
        int sequenceIndex,
        long lba,
        byte[] bytes,
        int reservedNonZero,
        string implementationIdentifier,
        string details)
        => new(
            sequence,
            sequenceIndex,
            lba,
            BitConverter.ToUInt16(bytes, 0),
            BitConverter.ToUInt16(bytes, 2),
            bytes[4],
            HasValidTagChecksum(bytes),
            BitConverter.ToUInt16(bytes, 6),
            BitConverter.ToUInt16(bytes, 8),
            BitConverter.ToUInt16(bytes, 10),
            HasValidDescriptorCrc(bytes),
            BitConverter.ToUInt32(bytes, 12),
            reservedNonZero,
            implementationIdentifier,
            bytes.ToArray(),
            Convert.ToHexString(SHA1.HashData(bytes)),
            details);

    private static bool HasValidTagChecksum(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 16)
            return false;
        int checksum = 0;
        for (int index = 0; index < 16; index++)
            if (index != 4)
                checksum += descriptor[index];
        return (byte)checksum == descriptor[4];
    }

    private static bool HasValidDescriptorCrc(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 16)
            return false;
        int length = BitConverter.ToUInt16(descriptor[10..12]);
        if (length < 0 || 16 + length > descriptor.Length)
            return false;
        ushort crc = 0;
        for (int index = 16; index < 16 + length; index++)
        {
            crc ^= (ushort)(descriptor[index] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc == BitConverter.ToUInt16(descriptor[8..10]);
    }

    private static string ReadEntityIdentifier(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 24 > bytes.Length)
            return string.Empty;
        return Encoding.ASCII.GetString(bytes.Slice(offset + 1, 23)).TrimEnd('\0', ' ');
    }

    private static int CountNonZero(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset >= bytes.Length)
            return 0;
        int end = Math.Min(bytes.Length, offset + length);
        int count = 0;
        for (int index = offset; index < end; index++)
            if (bytes[index] != 0)
                count++;
        return count;
    }
}
