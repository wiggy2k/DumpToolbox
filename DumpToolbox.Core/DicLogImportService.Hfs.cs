using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private static DicApplePartitionMapInfo? TryParseApplePartitionMap(
        IReadOnlyDictionary<long, DicPayloadEvidence> offsetEvidence)
    {
        if (!offsetEvidence.TryGetValue(0, out DicPayloadEvidence? sector0) || !sector0.IsComplete)
            return null;

        byte[] data = sector0.Data;
        var partitions = new List<DicApplePartitionEntry>();
        for (int offset = 512; offset + 512 <= data.Length; offset += 512)
        {
            if (data[offset] != (byte)'P' || data[offset + 1] != (byte)'M')
                continue;

            uint startBlock = ReadBeUInt32(data, offset + 8);
            uint blockCount = ReadBeUInt32(data, offset + 12);
            string name = ReadAsciiField(data, offset + 16, 32);
            string type = ReadAsciiField(data, offset + 48, 32);
            partitions.Add(new DicApplePartitionEntry(startBlock, blockCount, name, type));
        }

        return partitions.Count == 0 ? null : new DicApplePartitionMapInfo(partitions);
    }

    private static IReadOnlyList<DicHfsPartitionInspection> SynthesizeClassicHfsPhase1(
        DicVolumeInfo volume,
        IReadOnlyList<SkeletonContentEntry> entries,
        IReadOnlyList<DicHfsPartitionInspection> partitions,
        Dictionary<long, byte[]> metadata,
        List<string> warnings)
    {
        if (partitions.Count == 0)
            return partitions;

        var updated = new List<DicHfsPartitionInspection>(partitions.Count);
        foreach (DicHfsPartitionInspection partition in partitions)
        {
            if (partition.MasterDirectoryBlockPresentInDicEvidence || partition.MasterDirectoryBlock is not null)
            {
                updated.Add(partition);
                continue;
            }

            // Classic HFS supports at most 65,535 allocation blocks.  Toast commonly
            // rounds the allocation-block quantum to a 2048-byte boundary; choosing
            // enough 512-byte blocks to keep the count under ~60k reproduces the
            // observed 10 KiB allocation geometry on the first Toast 3.0.5 hybrid
            // without embedding title-specific constants.
            const ushort bitmapStartBlock = 3;
            const ushort firstAllocationBlock = 18;
            long usableAppleBlocks = Math.Max(0, (long)partition.BlockCount - firstAllocationBlock);
            int appleBlocksPerAllocation = Math.Max(1, (int)Math.Ceiling(usableAppleBlocks / 60000.0));
            appleBlocksPerAllocation = checked(((appleBlocksPerAllocation + 3) / 4) * 4);
            uint allocationBlockSize = checked((uint)appleBlocksPerAllocation * 512U);
            ushort allocationBlockCount = checked((ushort)Math.Min(ushort.MaxValue, usableAppleBlocks / appleBlocksPerAllocation));
            if (allocationBlockCount == 0)
            {
                updated.Add(partition);
                continue;
            }

            bool[] used = new bool[allocationBlockCount];
            long allocationAreaAbsoluteByte = checked(((long)partition.StartBlock + firstAllocationBlock) * 512L);
            long allocationAreaEndByte = checked(allocationAreaAbsoluteByte + (long)allocationBlockCount * allocationBlockSize);

            foreach (SkeletonContentEntry entry in entries)
            {
                if (entry.IsSpecial || entry.DataLength <= 0)
                    continue;

                MarkHfsAllocationRangeUsed(
                    used,
                    allocationAreaAbsoluteByte,
                    allocationAreaEndByte,
                    allocationBlockSize,
                    checked((long)entry.ExtentLba * CookedSectorSize),
                    entry.DataLength);

                if (entry.AlternateIsoRecords is { Count: > 0 })
                {
                    foreach (SkeletonAlternateIsoRecord alternate in entry.AlternateIsoRecords)
                    {
                        if (alternate.DataLength <= 0)
                            continue;
                        MarkHfsAllocationRangeUsed(
                            used,
                            allocationAreaAbsoluteByte,
                            allocationAreaEndByte,
                            allocationBlockSize,
                            checked((long)alternate.ExtentLba * CookedSectorSize),
                            alternate.DataLength);
                    }
                }
            }

            // Reserve one allocation block for each system B-tree as a structural
            // phase-1 scaffold.  We deliberately do not fabricate catalog records yet.
            ushort extentsStartBlock = FindFirstFreeHfsAllocationBlock(used, 0);
            if (extentsStartBlock < used.Length)
                used[extentsStartBlock] = true;
            ushort catalogStartBlock = FindFirstFreeHfsAllocationBlock(used, extentsStartBlock + 1);
            if (catalogStartBlock < used.Length)
                used[catalogStartBlock] = true;

            int usedCount = used.Count(value => value);
            int freeCount = allocationBlockCount - usedCount;

            string volumeName = BuildProvisionalHfsVolumeName(volume.VolumeIdentifier);
            ushort rootFileCount = checked((ushort)Math.Min(ushort.MaxValue, entries.Count(entry =>
                !entry.IsSpecial && (entry.IsoFileFlags & 0x04) == 0 && entry.Path.Count(c => c == '/') == 1)));
            ushort rootDirectoryCount = checked((ushort)Math.Min(ushort.MaxValue,
                volume.PrimaryPathTableRecords.Count(record => record.ParentDirectoryNumber == 1 && record.IdentifierLength > 1)));
            uint fileCount = checked((uint)entries.Count(entry => !entry.IsSpecial && (entry.IsoFileFlags & 0x04) == 0));
            uint directoryCount = checked((uint)Math.Max(0, volume.PrimaryPathTableRecords.Count - 1));
            uint nextCnid = checked(16U + fileCount + directoryCount + 1U);
            uint hfsTimestamp = DateTimeOffsetToHfsTimestamp(volume.DefaultRecordingTime);
            uint btreeFileSize = allocationBlockSize;

            var extentsRecord = extentsStartBlock < used.Length
                ? new[] { new DicHfsExtentDescriptor(extentsStartBlock, 1) }
                : Array.Empty<DicHfsExtentDescriptor>();
            var catalogRecord = catalogStartBlock < used.Length
                ? new[] { new DicHfsExtentDescriptor(catalogStartBlock, 1) }
                : Array.Empty<DicHfsExtentDescriptor>();

            byte[] mdb = BuildProvisionalHfsMdb(
                volumeName,
                hfsTimestamp,
                rootFileCount,
                rootDirectoryCount,
                fileCount,
                directoryCount,
                bitmapStartBlock,
                allocationBlockCount,
                allocationBlockSize,
                firstAllocationBlock,
                nextCnid,
                checked((ushort)Math.Min(ushort.MaxValue, freeCount)),
                btreeFileSize,
                extentsRecord,
                catalogRecord);
            WriteApplePartitionBytes(metadata, partition.StartBlock, 2L * 512L, mdb);

            byte[] bitmap = BuildHfsAllocationBitmap(used);
            WriteApplePartitionBytes(metadata, partition.StartBlock, (long)bitmapStartBlock * 512L, bitmap);

            if (extentsRecord.Length > 0)
            {
                byte[] tree = BuildEmptyHfsBTreeScaffold(allocationBlockSize, maxKeyLength: 7);
                long relative = checked((long)firstAllocationBlock * 512L + (long)extentsStartBlock * allocationBlockSize);
                WriteApplePartitionBytes(metadata, partition.StartBlock, relative, tree);
            }
            if (catalogRecord.Length > 0)
            {
                byte[] tree = BuildEmptyHfsBTreeScaffold(allocationBlockSize, maxKeyLength: 37);
                long relative = checked((long)firstAllocationBlock * 512L + (long)catalogStartBlock * allocationBlockSize);
                WriteApplePartitionBytes(metadata, partition.StartBlock, relative, tree);
            }

            var synthesized = new DicHfsMasterDirectoryBlock(
                volumeName,
                rootFileCount,
                bitmapStartBlock,
                allocationBlockCount,
                allocationBlockSize,
                firstAllocationBlock,
                nextCnid,
                checked((ushort)Math.Min(ushort.MaxValue, freeCount)),
                btreeFileSize,
                extentsRecord,
                btreeFileSize,
                catalogRecord);

            updated.Add(partition with
            {
                Phase1Synthesized = true,
                SynthesizedMasterDirectoryBlock = synthesized,
                SynthesizedBitmapUsedBlocks = usedCount,
                SynthesizedBitmapFreeBlocks = freeCount
            });

            warnings.Add(
                $"Experimental classic-HFS phase-1 synthesis enabled for '{partition.Name}': generated MDB at LBA {partition.MasterDirectoryBlockLba:N0}+0x{partition.MasterDirectoryBlockByteOffset:X3}, " +
                $"allocation bitmap ({allocationBlockCount:N0} block bits) and one-block empty Extents/Catalog B-tree scaffolds. " +
                "This is structural recovery only; exact Toast catalog ordering, Mac-only file selection, CNIDs, Finder metadata, resource-fork ownership and original timestamps still require catalog evidence or a Mac source tree.");
        }

        return updated;
    }

    private static void MarkHfsAllocationRangeUsed(
        bool[] used,
        long allocationAreaStart,
        long allocationAreaEnd,
        uint allocationBlockSize,
        long fileStart,
        long fileLength)
    {
        if (fileLength <= 0)
            return;
        long fileEnd;
        if (fileLength > 0 && fileStart > long.MaxValue - fileLength)
            fileEnd = long.MaxValue;
        else
            fileEnd = fileStart + fileLength;
        long overlapStart = Math.Max(fileStart, allocationAreaStart);
        long overlapEnd = Math.Min(fileEnd, allocationAreaEnd);
        if (overlapStart >= overlapEnd)
            return;

        long firstLong = (overlapStart - allocationAreaStart) / allocationBlockSize;
        long lastLong = (overlapEnd - 1 - allocationAreaStart) / allocationBlockSize;
        int first = firstLong <= 0 ? 0 : firstLong >= used.Length ? used.Length : (int)firstLong;
        int last = lastLong < 0 ? -1 : lastLong >= used.Length ? used.Length - 1 : (int)lastLong;
        first = Math.Max(0, first);
        last = Math.Min(used.Length - 1, last);
        for (int i = first; i <= last; i++)
            used[i] = true;
    }

    private static ushort FindFirstFreeHfsAllocationBlock(bool[] used, int start)
    {
        for (int i = Math.Max(0, start); i < used.Length; i++)
        {
            if (!used[i])
                return checked((ushort)i);
        }
        return ushort.MaxValue;
    }

    private static string BuildProvisionalHfsVolumeName(string isoVolumeIdentifier)
    {
        string value = (isoVolumeIdentifier ?? string.Empty).Trim().Replace('_', ' ');
        if (value.Length == 0)
            value = "HFS VOLUME";
        return value.Length <= 27 ? value : value[..27];
    }

    private static uint DateTimeOffsetToHfsTimestamp(DateTimeOffset value)
    {
        DateTimeOffset epoch = new(new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        // Do not use a checked floating-point-to-integer conversion here. Some
        // imported ISO timestamps are deliberately absent/defaulted and can sit
        // outside the representable HFS timestamp interval. Saturate instead.
        double seconds = (value.ToUniversalTime() - epoch).TotalSeconds;
        if (double.IsNaN(seconds) || seconds <= 0)
            return 0;
        if (seconds >= uint.MaxValue)
            return uint.MaxValue;
        return (uint)seconds;
    }

    private static byte[] BuildProvisionalHfsMdb(
        string volumeName,
        uint timestamp,
        ushort rootFileCount,
        ushort rootDirectoryCount,
        uint fileCount,
        uint directoryCount,
        ushort bitmapStartBlock,
        ushort allocationBlockCount,
        uint allocationBlockSize,
        ushort firstAllocationBlock,
        uint nextCnid,
        ushort freeBlocks,
        uint btreeFileSize,
        IReadOnlyList<DicHfsExtentDescriptor> extentsRecord,
        IReadOnlyList<DicHfsExtentDescriptor> catalogRecord)
    {
        byte[] mdb = new byte[512];
        WriteBeUInt16(mdb, 0, 0x4244);
        WriteBeUInt32(mdb, 2, timestamp);
        WriteBeUInt32(mdb, 6, timestamp);
        WriteBeUInt16(mdb, 10, 0x0100);
        WriteBeUInt16(mdb, 12, rootFileCount);
        WriteBeUInt16(mdb, 14, bitmapStartBlock);
        WriteBeUInt16(mdb, 16, 0);
        WriteBeUInt16(mdb, 18, allocationBlockCount);
        WriteBeUInt32(mdb, 20, allocationBlockSize);
        WriteBeUInt32(mdb, 24, checked(allocationBlockSize * 4U));
        WriteBeUInt16(mdb, 28, firstAllocationBlock);
        WriteBeUInt32(mdb, 30, nextCnid);
        WriteBeUInt16(mdb, 34, freeBlocks);
        byte[] name = Encoding.Latin1.GetBytes(volumeName);
        int nameLength = Math.Min(27, name.Length);
        mdb[36] = checked((byte)nameLength);
        name.AsSpan(0, nameLength).CopyTo(mdb.AsSpan(37, nameLength));
        WriteBeUInt32(mdb, 70, checked(fileCount + directoryCount + 1U));
        WriteBeUInt32(mdb, 74, checked(allocationBlockSize * 102U));
        WriteBeUInt32(mdb, 78, checked(allocationBlockSize * 102U));
        WriteBeUInt16(mdb, 82, rootDirectoryCount);
        WriteBeUInt32(mdb, 84, fileCount);
        WriteBeUInt32(mdb, 88, directoryCount);
        WriteBeUInt32(mdb, 130, btreeFileSize);
        WriteHfsExtentRecord(mdb, 134, extentsRecord);
        WriteBeUInt32(mdb, 146, btreeFileSize);
        WriteHfsExtentRecord(mdb, 150, catalogRecord);
        return mdb;
    }

    private static void WriteHfsExtentRecord(byte[] destination, int offset, IReadOnlyList<DicHfsExtentDescriptor> extents)
    {
        for (int i = 0; i < Math.Min(3, extents.Count); i++)
        {
            WriteBeUInt16(destination, offset + i * 4, extents[i].StartBlock);
            WriteBeUInt16(destination, offset + i * 4 + 2, extents[i].BlockCount);
        }
    }

    private static byte[] BuildHfsAllocationBitmap(bool[] used)
    {
        byte[] bitmap = new byte[(used.Length + 7) / 8];
        for (int i = 0; i < used.Length; i++)
        {
            if (used[i])
                bitmap[i >> 3] |= checked((byte)(0x80 >> (i & 7)));
        }
        return bitmap;
    }

    private static byte[] BuildEmptyHfsBTreeScaffold(uint allocationBlockSize, ushort maxKeyLength)
    {
        int length = checked((int)allocationBlockSize);
        byte[] file = new byte[length];
        const ushort nodeSize = 512;
        if (file.Length < nodeSize)
            return file;

        // Header node descriptor.
        file[8] = 0x01; // kBTHeaderNode
        file[9] = 0x00;
        WriteBeUInt16(file, 10, 3); // header, user, map records

        const int headerOffset = 14;
        WriteBeUInt16(file, headerOffset + 0, 0);   // treeDepth
        WriteBeUInt32(file, headerOffset + 2, 0);   // rootNode
        WriteBeUInt32(file, headerOffset + 6, 0);   // leafRecords
        WriteBeUInt32(file, headerOffset + 10, 0);  // firstLeafNode
        WriteBeUInt32(file, headerOffset + 14, 0);  // lastLeafNode
        WriteBeUInt16(file, headerOffset + 18, nodeSize);
        WriteBeUInt16(file, headerOffset + 20, maxKeyLength);
        uint totalNodes = checked((uint)(file.Length / nodeSize));
        WriteBeUInt32(file, headerOffset + 22, totalNodes);
        WriteBeUInt32(file, headerOffset + 26, totalNodes > 0 ? totalNodes - 1 : 0);
        WriteBeUInt16(file, headerOffset + 30, 0);
        WriteBeUInt32(file, headerOffset + 32, allocationBlockSize);

        const ushort userOffset = 120;
        const ushort mapOffset = 248;
        ushort freeOffset = 504;
        // Mark header node allocated in the node map record.
        file[mapOffset] = 0x80;
        WriteBeUInt16(file, 510, headerOffset);
        WriteBeUInt16(file, 508, userOffset);
        WriteBeUInt16(file, 506, mapOffset);
        WriteBeUInt16(file, 504, freeOffset);
        return file;
    }

    private static void WriteApplePartitionBytes(
        Dictionary<long, byte[]> metadata,
        uint partitionStartAppleBlock,
        long relativeByteOffset,
        ReadOnlySpan<byte> source)
    {
        long absoluteByte = checked((long)partitionStartAppleBlock * 512L + relativeByteOffset);
        int copied = 0;
        while (copied < source.Length)
        {
            long currentAbsolute = absoluteByte + copied;
            long lba = currentAbsolute / CookedSectorSize;
            int offset = checked((int)(currentAbsolute % CookedSectorSize));
            int count = Math.Min(source.Length - copied, CookedSectorSize - offset);
            if (!metadata.TryGetValue(lba, out byte[]? payload) || payload.Length != CookedSectorSize)
            {
                payload = new byte[CookedSectorSize];
                metadata[lba] = payload;
            }
            source.Slice(copied, count).CopyTo(payload.AsSpan(offset, count));
            copied += count;
        }
    }

    private static void WriteBeUInt16(byte[] destination, int offset, ushort value)
    {
        destination[offset] = checked((byte)(value >> 8));
        destination[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteBeUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)((value >> 24) & 0xFF);
        destination[offset + 1] = (byte)((value >> 16) & 0xFF);
        destination[offset + 2] = (byte)((value >> 8) & 0xFF);
        destination[offset + 3] = (byte)(value & 0xFF);
    }

    private static IReadOnlyList<DicHfsPartitionInspection> BuildHfsPartitionInspections(
        DicApplePartitionMapInfo? partitionMap,
        IReadOnlyDictionary<long, byte[]> metadata)
    {
        if (partitionMap is null)
            return Array.Empty<DicHfsPartitionInspection>();

        var results = new List<DicHfsPartitionInspection>();
        foreach (DicApplePartitionEntry partition in partitionMap.Partitions
                     .Where(item => item.Type.Equals("Apple_HFS", StringComparison.OrdinalIgnoreCase)))
        {
            (long partitionLba, int partitionOffset) = AbsoluteAppleBlockToCdLocation(partition.StartBlock);
            (long mdbLba, int mdbOffset) = AbsoluteAppleBlockToCdLocation(checked(partition.StartBlock + 2));
            (long defaultBitmapLba, int defaultBitmapOffset) = AbsoluteAppleBlockToCdLocation(checked(partition.StartBlock + 3));

            DicHfsMasterDirectoryBlock? mdb = TryParseHfsMasterDirectoryBlock(metadata, mdbLba, mdbOffset);
            bool hasMdbEvidence = mdb is not null;

            long bitmapLba = defaultBitmapLba;
            int bitmapOffset = defaultBitmapOffset;
            if (mdb is not null)
                (bitmapLba, bitmapOffset) = AbsoluteAppleBlockToCdLocation(checked(partition.StartBlock + mdb.VolumeBitmapStartBlock));

            results.Add(new DicHfsPartitionInspection(
                partition.Name,
                partition.Type,
                partition.StartBlock,
                partition.BlockCount,
                partitionLba,
                partitionOffset,
                mdbLba,
                mdbOffset,
                bitmapLba,
                bitmapOffset,
                hasMdbEvidence,
                mdb));
        }

        return results;
    }

    private static (long Lba, int ByteOffset) AbsoluteAppleBlockToCdLocation(uint block)
    {
        long absoluteByte = checked(block * 512L);
        return (absoluteByte / CookedSectorSize, checked((int)(absoluteByte % CookedSectorSize)));
    }

    private static DicHfsMasterDirectoryBlock? TryParseHfsMasterDirectoryBlock(
        IReadOnlyDictionary<long, byte[]> metadata,
        long lba,
        int byteOffset)
    {
        if (!metadata.TryGetValue(lba, out byte[]? data) || byteOffset < 0 || byteOffset + 162 > data.Length)
            return null;
        if (ReadBeUInt16(data, byteOffset) != 0x4244)
            return null;

        ushort fileCountInRoot = ReadBeUInt16(data, byteOffset + 12);
        ushort bitmapStart = ReadBeUInt16(data, byteOffset + 14);
        ushort allocationBlockCount = ReadBeUInt16(data, byteOffset + 18);
        uint allocationBlockSize = ReadBeUInt32(data, byteOffset + 20);
        ushort firstAllocationBlock = ReadBeUInt16(data, byteOffset + 28);
        uint nextCatalogId = ReadBeUInt32(data, byteOffset + 30);
        ushort freeBlocks = ReadBeUInt16(data, byteOffset + 34);
        int nameLength = Math.Min(data[byteOffset + 36], (byte)27);
        string volumeName = Encoding.Latin1.GetString(data, byteOffset + 37, nameLength);

        uint extentsFileSize = ReadBeUInt32(data, byteOffset + 130);
        IReadOnlyList<DicHfsExtentDescriptor> extentsFileExtents = ReadHfsExtentRecord(data, byteOffset + 134);
        uint catalogFileSize = ReadBeUInt32(data, byteOffset + 146);
        IReadOnlyList<DicHfsExtentDescriptor> catalogExtents = ReadHfsExtentRecord(data, byteOffset + 150);

        return new DicHfsMasterDirectoryBlock(
            volumeName,
            fileCountInRoot,
            bitmapStart,
            allocationBlockCount,
            allocationBlockSize,
            firstAllocationBlock,
            nextCatalogId,
            freeBlocks,
            extentsFileSize,
            extentsFileExtents,
            catalogFileSize,
            catalogExtents);
    }

    private static IReadOnlyList<DicHfsExtentDescriptor> ReadHfsExtentRecord(byte[] data, int offset)
    {
        var extents = new List<DicHfsExtentDescriptor>(3);
        for (int i = 0; i < 3; i++)
        {
            ushort startBlock = ReadBeUInt16(data, offset + i * 4);
            ushort blockCount = ReadBeUInt16(data, offset + i * 4 + 2);
            if (blockCount > 0)
                extents.Add(new DicHfsExtentDescriptor(startBlock, blockCount));
        }
        return extents;
    }

    private static ushort ReadBeUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadBeUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24) |
           ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) |
           data[offset + 3];

    private static string ReadAsciiField(byte[] data, int offset, int length)
    {
        int end = offset;
        int max = Math.Min(data.Length, offset + length);
        while (end < max && data[end] != 0)
            end++;
        return Encoding.ASCII.GetString(data, offset, end - offset).Trim();
    }

}
