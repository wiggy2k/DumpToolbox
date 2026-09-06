using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

internal sealed record DiscVolumeDescriptorEvidence(
    string Namespace,
    byte DescriptorType,
    long DescriptorLba,
    int DescriptorSequence,
    string SystemId,
    string VolumeId,
    string PublisherId,
    string DataPreparerId,
    string ApplicationId,
    uint VolumeSpaceSize,
    string EscapeSequence,
    uint PathTableSize,
    uint TypeLPathTableLba,
    uint OptionalTypeLPathTableLba,
    uint TypeMPathTableLba,
    uint OptionalTypeMPathTableLba,
    uint RootExtent,
    uint RootLength,
    byte RootRecordLength,
    byte[] RootSystemUse);

internal sealed record DiscFilesystemRecordEvidence(
    string Namespace,
    string Path,
    string ParentPath,
    string Identifier,
    byte[] IdentifierBytes,
    uint Extent,
    uint Length,
    byte Flags,
    bool IsDirectory,
    DateTimeOffset? RecordingTime,
    byte[] RawRecordingTime,
    uint DirectoryExtent,
    int RecordOffset,
    int RecordIndex,
    byte? IdentifierPaddingByte,
    byte[] SystemUse,
    byte[] RawRecord)
{
    public DiscFilesystemRecordEvidence(
        string namespaceName, string path, string parentPath, string identifier, byte[] identifierBytes,
        uint extent, uint length, byte flags, bool isDirectory, DateTimeOffset? recordingTime,
        byte[] rawRecordingTime, uint directoryExtent, int recordOffset, int recordIndex)
        : this(namespaceName, path, parentPath, identifier, identifierBytes, extent, length, flags,
            isDirectory, recordingTime, rawRecordingTime, directoryExtent, recordOffset, recordIndex,
            null, [], [])
    {
    }
}

internal readonly record struct DiscEvidenceCandidateCounts(int BeforeTimestamp, int? AfterTimestamp);

internal sealed record DiscPathTableRecordEvidence(
    string Namespace,
    string TableKind,
    uint TableLba,
    int RecordIndex,
    int RecordOffset,
    int DirectoryNumber,
    ushort ParentDirectoryNumber,
    uint Extent,
    string Identifier,
    byte[] IdentifierBytes);

internal static class DiscMasteringOrderingExtractor
{
    public static async Task<List<DiscVolumeDescriptorEvidence>> ReadDescriptorsAsync(
        Func<long, CancellationToken, Task<byte[]>> readSector,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<DiscVolumeDescriptorEvidence>();
        for (long lba = 16; lba < 256; lba++)
        {
            byte[] sector = await readSector(lba, cancellationToken).ConfigureAwait(false);
            if (sector.Length < 2048 || Encoding.ASCII.GetString(sector, 1, 5) != "CD001")
                continue;

            byte type = sector[0];
            string escapeSequence = type == 2 ? Encoding.ASCII.GetString(sector, 88, 3) : string.Empty;
            string descriptorNamespace = type switch
            {
                0 => "BOOT",
                1 => "ISO9660",
                2 when escapeSequence is "%/@" or "%/C" or "%/E" => "JOLIET",
                2 => "SUPPLEMENTARY",
                255 => "TERMINATOR",
                _ => $"TYPE_{type}"
            };
            descriptors.Add(ParseDescriptor(sector, descriptorNamespace, type, lba, descriptors.Count,
                escapeSequence));
            if (type == 255)
                break;
        }
        return descriptors;
    }

    public static async Task<List<DiscFilesystemRecordEvidence>> ReadTreeAsync(
        Func<uint, uint, CancellationToken, Task<byte[]>> readBytes,
        DiscVolumeDescriptorEvidence descriptor,
        CancellationToken cancellationToken)
    {
        bool joliet = descriptor.Namespace == "JOLIET";
        var result = new List<DiscFilesystemRecordEvidence>();
        var seen = new HashSet<uint>();

        async Task Walk(uint extent, uint length, string parent)
        {
            if (!seen.Add(extent))
                return;
            byte[] data = await readBytes(extent, length, cancellationToken).ConfigureAwait(false);
            int offset = 0;
            int recordIndex = 0;
            while (offset < data.Length)
            {
                int recordLength = data[offset];
                if (recordLength == 0)
                {
                    offset = ((offset / 2048) + 1) * 2048;
                    continue;
                }
                if (offset + recordLength > data.Length)
                    break;
                if (recordLength < 34)
                {
                    offset += recordLength;
                    recordIndex++;
                    continue;
                }

                uint childExtent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 2, 4));
                uint childLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 10, 4));
                byte flags = data[offset + 25];
                int identifierLength = data[offset + 32];
                if (33 + identifierLength > recordLength)
                {
                    offset += recordLength;
                    recordIndex++;
                    continue;
                }

                byte[] identifierBytes = data.AsSpan(offset + 33, identifierLength).ToArray();
                bool specialDirectory = identifierLength == 1 && identifierBytes[0] is 0 or 1;
                if (!specialDirectory)
                {
                    string identifier = joliet
                        ? DecodeJoliet(identifierBytes)
                        : Encoding.ASCII.GetString(identifierBytes);
                    int versionSeparator = identifier.LastIndexOf(';');
                    if (versionSeparator >= 0)
                        identifier = identifier[..versionSeparator];
                    string path = parent == "/" ? "/" + identifier : parent + "/" + identifier;
                    bool directory = (flags & 2) != 0;
                    byte[] rawRecordingTime = data.AsSpan(offset + 18, 7).ToArray();
                    byte? identifierPaddingByte = (identifierLength & 1) == 0 && 33 + identifierLength < recordLength
                        ? data[offset + 33 + identifierLength]
                        : null;
                    int systemUseOffset = 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);
                    byte[] systemUse = systemUseOffset < recordLength
                        ? data.AsSpan(offset + systemUseOffset, recordLength - systemUseOffset).ToArray()
                        : [];
                    byte[] rawRecord = data.AsSpan(offset, recordLength).ToArray();
                    DateTimeOffset? recordingTime = TryReadIsoRecordingTime(rawRecordingTime, out DateTimeOffset parsedRecordingTime)
                        ? parsedRecordingTime
                        : null;
                    result.Add(new DiscFilesystemRecordEvidence(
                        descriptor.Namespace, path, parent, identifier, identifierBytes, childExtent, childLength,
                        flags, directory, recordingTime, rawRecordingTime, extent, offset, recordIndex,
                        identifierPaddingByte, systemUse, rawRecord));
                    if (directory && childLength > 0)
                        await Walk(childExtent, childLength, path).ConfigureAwait(false);
                }
                offset += recordLength;
                recordIndex++;
            }
        }

        await Walk(descriptor.RootExtent, descriptor.RootLength, "/").ConfigureAwait(false);
        return result;
    }

    public static async Task<List<DiscPathTableRecordEvidence>> ReadPathTablesAsync(
        Func<uint, uint, CancellationToken, Task<byte[]>> readBytes,
        DiscVolumeDescriptorEvidence descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.PathTableSize == 0)
            return [];

        var result = new List<DiscPathTableRecordEvidence>();
        (string Kind, uint Lba, bool BigEndian)[] tables =
        [
            ("L", descriptor.TypeLPathTableLba, false),
            ("L_OPTIONAL", descriptor.OptionalTypeLPathTableLba, false),
            ("M", descriptor.TypeMPathTableLba, true),
            ("M_OPTIONAL", descriptor.OptionalTypeMPathTableLba, true)
        ];
        foreach (IGrouping<uint, (string Kind, uint Lba, bool BigEndian)> tableGroup in tables
                     .Where(table => table.Lba != 0)
                     .GroupBy(table => table.Lba))
        {
            (string Kind, uint Lba, bool BigEndian)[] aliases = tableGroup.ToArray();
            uint lba = tableGroup.Key;
            byte[] bytes = await readBytes(lba, descriptor.PathTableSize, cancellationToken).ConfigureAwait(false);
            bool bigEndian = aliases[0].BigEndian;
            string kind = aliases[0].Kind;

            if (aliases.Select(alias => alias.BigEndian).Distinct().Count() > 1)
            {
                bool littleEndianRootMatches = PathTableRootMatches(bytes, descriptor.RootExtent, bigEndian: false);
                bool bigEndianRootMatches = PathTableRootMatches(bytes, descriptor.RootExtent, bigEndian: true);

                // Some mastering programs place every L/M pointer on the same physical
                // table. Decode it only in the byte order proved by its root entry; the
                // opposite interpretation produces plausible-looking but false rows.
                if (littleEndianRootMatches == bigEndianRootMatches)
                    continue;

                bigEndian = bigEndianRootMatches;
                kind = $"{string.Join("+", aliases.Select(alias => alias.Kind))}_ALIASED_{(bigEndian ? "M" : "L")}";
            }
            else if (aliases.Length > 1)
            {
                kind = $"{string.Join("+", aliases.Select(alias => alias.Kind))}_ALIASED";
            }

            int offset = 0;
            int recordIndex = 0;
            while (offset + 8 <= bytes.Length)
            {
                int identifierLength = bytes[offset];
                if (identifierLength == 0 || offset + 8 + identifierLength > bytes.Length)
                    break;
                uint extent = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 2, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 2, 4));
                ushort parentNumber = bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 6, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6, 2));
                byte[] identifierBytes = bytes.AsSpan(offset + 8, identifierLength).ToArray();
                string identifier = identifierLength == 1 && identifierBytes[0] == 0
                    ? "/"
                    : descriptor.Namespace == "JOLIET"
                        ? DecodeJoliet(identifierBytes)
                        : Encoding.ASCII.GetString(identifierBytes);
                result.Add(new DiscPathTableRecordEvidence(
                    descriptor.Namespace, kind, lba, recordIndex, offset, recordIndex + 1, parentNumber, extent,
                    identifier, identifierBytes));
                offset += 8 + identifierLength + (identifierLength & 1);
                recordIndex++;
            }
        }
        return result;
    }

    internal static bool TryReadIsoRecordingTime(ReadOnlySpan<byte> raw, out DateTimeOffset value)
    {
        value = default;
        if (raw.Length < 7)
            return false;

        try
        {
            int year = 1900 + raw[0];
            int month = raw[1];
            int day = raw[2];
            int hour = raw[3];
            int minute = raw[4];
            int second = raw[5];
            sbyte quarterHours = unchecked((sbyte)raw[6]);
            if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59 ||
                quarterHours is < -48 or > 52)
            {
                return false;
            }

            value = new DateTimeOffset(year, month, day, hour, minute, second,
                TimeSpan.FromMinutes(quarterHours * 15));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool PathTableRootMatches(ReadOnlySpan<byte> bytes, uint expectedRootExtent, bool bigEndian)
    {
        if (bytes.Length < 9 || bytes[0] != 1 || bytes[8] != 0)
            return false;
        uint extent = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(2, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(2, 4));
        ushort parent = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        return extent == expectedRootExtent && parent == 1;
    }

    private static DiscVolumeDescriptorEvidence ParseDescriptor(
        byte[] sector,
        string descriptorNamespace,
        byte descriptorType,
        long lba,
        int sequence,
        string escapeSequence)
    {
        string Ascii(int offset, int length) => Encoding.ASCII.GetString(sector, offset, length).TrimEnd('\0', ' ');
        uint rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(158, 4));
        uint rootLength = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(166, 4));
        byte rootRecordLength = sector[156];
        byte[] rootSystemUse = [];
        if (rootRecordLength >= 34 && 156 + rootRecordLength <= sector.Length)
        {
            ReadOnlySpan<byte> rootRecord = sector.AsSpan(156, rootRecordLength);
            int identifierLength = rootRecord[32];
            int systemUseOffset = 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);
            if (systemUseOffset < rootRecord.Length)
                rootSystemUse = rootRecord[systemUseOffset..].ToArray();
        }

        return new DiscVolumeDescriptorEvidence(
            descriptorNamespace, descriptorType, lba, sequence,
            Ascii(8, 32), Ascii(40, 32), Ascii(318, 128), Ascii(446, 128), Ascii(574, 128),
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80, 4)), escapeSequence,
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(132, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(140, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(144, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(sector.AsSpan(148, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(sector.AsSpan(152, 4)),
            rootExtent, rootLength, rootRecordLength, rootSystemUse);
    }

    internal static string DecodeJoliet(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[bytes.Length / 2];
        for (int index = 0; index < characters.Length; index++)
            characters[index] = (char)((bytes[index * 2] << 8) | bytes[index * 2 + 1]);
        return new string(characters).TrimEnd('\0');
    }
}
