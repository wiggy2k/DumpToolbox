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
    uint DirectoryExtent,
    int RecordOffset,
    int RecordIndex);

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
                    result.Add(new DiscFilesystemRecordEvidence(
                        descriptor.Namespace, path, parent, identifier, identifierBytes, childExtent, childLength,
                        flags, directory, extent, offset, recordIndex));
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
        foreach ((string kind, uint lba, bool bigEndian) in tables)
        {
            if (lba == 0)
                continue;
            byte[] bytes = await readBytes(lba, descriptor.PathTableSize, cancellationToken).ConfigureAwait(false);
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
