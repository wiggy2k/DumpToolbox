using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

/// <summary>
/// Shared recognition for Nero's length-prefixed NRI payload signature and the
/// non-standard ISO9660 directory record Nero embeds in another record's System Use area.
/// </summary>
internal static class NeroNriDetector
{
    internal sealed record EmbeddedDirectoryRecord(
        string FileName,
        uint ExtentLba,
        uint DataLength,
        int RecordOffset,
        int RecordLength,
        byte FileFlags,
        int ExtendedAttributeRecordLength,
        int FileUnitSize,
        int InterleaveGapSize,
        int IdentifierLength);

    internal sealed record SystemAreaRecord(
        string FileName,
        uint ExtentLba,
        uint DataLength,
        uint PrivateValue);

    internal static bool IsProjectFileName(string value)
    {
        if (value.Length != 12 ||
            !value.StartsWith("!!MS", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(".NRI", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.AsSpan(4, 4).ToString().All(Uri.IsHexDigit);
    }

    internal static bool IsLikelyStandaloneProjectFile(string path)
    {
        string fileName = Path.GetFileName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        return Path.GetExtension(path).Equals(".nri", StringComparison.OrdinalIgnoreCase) ||
               IsProjectFileName(fileName) ||
               (stem.Length == 40 && stem.All(Uri.IsHexDigit));
    }

    internal static bool CouldContainPayloadSignature(long length)
        => length is >= 8 and <= 16 * 1024 * 1024;

    internal static bool TryReadPayloadSignature(ReadOnlySpan<byte> payload, out string signature)
    {
        signature = string.Empty;
        if (payload.Length < 8)
            return false;

        int length = payload[0];
        if (length < 7 || length > payload.Length - 1 ||
            !payload.Slice(1, 7).SequenceEqual("NeroISO"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> value = payload.Slice(1, length);
        foreach (byte item in value)
        {
            if (item is < 0x20 or > 0x7e)
                return false;
        }

        signature = Encoding.ASCII.GetString(value);
        return true;
    }

    internal static bool TryParseEmbeddedDirectoryRecord(
        ReadOnlySpan<byte> outerRecord,
        out EmbeddedDirectoryRecord? record)
    {
        record = null;
        if (outerRecord.Length < 34 || outerRecord[0] < 34 || outerRecord[0] > outerRecord.Length)
            return false;

        int outerLength = outerRecord[0];
        int identifierLength = outerRecord[32];
        int systemUseOffset = 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);
        if (systemUseOffset >= outerLength)
            return false;

        for (int offset = systemUseOffset; offset + 34 <= outerLength; offset++)
        {
            int length = outerRecord[offset];
            if (length < 34 || offset + length > outerLength)
                continue;

            int nestedIdentifierLength = outerRecord[offset + 32];
            if (nestedIdentifierLength <= 0 || offset + 33 + nestedIdentifierLength > offset + length)
                continue;

            uint littleLba = BinaryPrimitives.ReadUInt32LittleEndian(outerRecord.Slice(offset + 2, 4));
            uint bigLba = BinaryPrimitives.ReadUInt32BigEndian(outerRecord.Slice(offset + 6, 4));
            uint littleLength = BinaryPrimitives.ReadUInt32LittleEndian(outerRecord.Slice(offset + 10, 4));
            uint bigLength = BinaryPrimitives.ReadUInt32BigEndian(outerRecord.Slice(offset + 14, 4));
            if (littleLba != bigLba || littleLength != bigLength || littleLength == 0 ||
                (outerRecord[offset + 25] & 0x03) != 0x01)
            {
                continue;
            }

            string identifier = Encoding.ASCII.GetString(
                outerRecord.Slice(offset + 33, nestedIdentifierLength));
            string candidateName = StripIsoVersion(identifier);
            if (!IsProjectFileName(candidateName))
                continue;

            record = new EmbeddedDirectoryRecord(
                candidateName.ToUpperInvariant(),
                littleLba,
                littleLength,
                offset,
                length,
                outerRecord[offset + 25],
                outerRecord[offset + 1],
                outerRecord[offset + 26],
                outerRecord[offset + 27],
                nestedIdentifierLength);
            return true;
        }

        return false;
    }

    internal static bool TryParseSystemAreaRecord(
        ReadOnlySpan<byte> sector15,
        out SystemAreaRecord? record)
    {
        record = null;
        if (sector15.Length < 32)
            return false;

        int nameLength = sector15[0];
        if (nameLength != 12 || 1 + nameLength + 19 > sector15.Length)
            return false;

        string fileName = Encoding.ASCII.GetString(sector15.Slice(1, nameLength));
        if (!IsProjectFileName(fileName) ||
            !sector15.Slice(21, 3).SequenceEqual(new byte[] { 0, 0, 0 }) ||
            !sector15.Slice(28, 4).SequenceEqual(new byte[] { 0, 0, 0, 0x20 }))
        {
            return false;
        }

        record = new SystemAreaRecord(
            fileName.ToUpperInvariant(),
            BinaryPrimitives.ReadUInt32LittleEndian(sector15.Slice(13, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector15.Slice(17, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(sector15.Slice(24, 4)));
        return true;
    }

    private static string StripIsoVersion(string value)
    {
        int semicolon = value.LastIndexOf(';');
        return semicolon > 0 && semicolon < value.Length - 1 && value[(semicolon + 1)..].All(char.IsDigit)
            ? value[..semicolon]
            : value;
    }
}

public sealed record NeroNriProjectInfo(
    string FileName,
    uint ExtentLba,
    uint DataLength,
    string NeroIsoSignature,
    string Sha1,
    bool HasMatchingSystemAreaRecord,
    uint? SystemAreaPrivateValue,
    string DirectoryNamespaces = "ISO9660");

public sealed record NeroNriPayloadRequirement(
    string FileName,
    uint ExtentLba,
    uint DataLength,
    string Evidence);
