using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{
    private static async Task<DonorFilesystem> ParseFilesystemAsync(
        DonorImageReader image,
        CancellationToken cancellationToken)
    {
        byte[]? pvd = null;
        byte[]? jolietSvd = null;
        var metadata = new HashSet<long>();

        for (long lba = SystemAreaSectors; lba < SystemAreaSectors + 64; lba++)
        {
            byte[] sector;
            try { sector = await image.ReadForm1SectorAsync(lba, cancellationToken).ConfigureAwait(false); }
            catch { break; }

            if (!sector.AsSpan(1, 5).SequenceEqual(Cd001))
                break;

            if (sector[0] == 1 && pvd is null)
            {
                pvd = sector;
                metadata.Add(lba);
            }
            else if (sector[0] == 2)
            {
                if (jolietSvd is null && IsJolietDescriptor(sector))
                    jolietSvd = sector;
                // Supplementary descriptor sectors are still not donor metadata authority:
                // they are parsed here only to recover the user-visible Joliet namespace.
            }
            else
            {
                // Preserve non-supplementary descriptor records such as the terminator
                // and boot records when a same-disc donor is applied.
                metadata.Add(lba);
            }

            if (sector[0] == 0xFF)
                break;
        }

        if (pvd is null)
            return new DonorFilesystem(
                null,
                string.Empty,
                jolietSvd is not null,
                Array.Empty<DicDonorFile>(),
                Array.Empty<DicDonorFile>(),
                metadata,
                null,
                Array.Empty<NeroNriPayloadRequirement>());

        string volumeId = Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd(' ', '\0');
        var primaryFiles = new List<DicDonorFile>();
        var primaryNeroRequirements = new List<NeroNriPayloadRequirement>();
        await CollectDescriptorMetadataAndTreeAsync(
            image, pvd, false, primaryFiles, primaryNeroRequirements, metadata, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DicDonorFile> combinedPrimaryFiles = CombineMultiExtentDonorFiles(primaryFiles);

        IReadOnlyList<DicDonorFile> combinedJolietFiles = Array.Empty<DicDonorFile>();
        if (jolietSvd is not null)
        {
            // Joliet metadata is intentionally collected into a throw-away set so it does
            // not become eligible for same-disc primary metadata copying.
            var jolietFiles = new List<DicDonorFile>();
            var jolietNeroRequirements = new List<NeroNriPayloadRequirement>();
            var jolietMetadata = new HashSet<long>();
            await CollectDescriptorMetadataAndTreeAsync(
                image, jolietSvd, true, jolietFiles, jolietNeroRequirements, jolietMetadata, cancellationToken).ConfigureAwait(false);
            combinedJolietFiles = CombineMultiExtentDonorFiles(jolietFiles);
            primaryNeroRequirements.AddRange(jolietNeroRequirements);
        }

        var mergedPrimaryFiles = combinedPrimaryFiles.ToList();
        foreach (DicDonorFile jolietNri in combinedJolietFiles.Where(file => file.IsEmbeddedNeroProject))
        {
            int match = mergedPrimaryFiles.FindIndex(file =>
                file.IsEmbeddedNeroProject &&
                file.ExtentLba == jolietNri.ExtentLba &&
                file.DataLength == jolietNri.DataLength &&
                Path.GetFileName(file.Path).Equals(Path.GetFileName(jolietNri.Path), StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                mergedPrimaryFiles[match] = mergedPrimaryFiles[match] with
                {
                    NeroDirectoryNamespaces = "ISO9660+Joliet"
                };
            }
            else
            {
                mergedPrimaryFiles.Add(jolietNri);
            }
        }

        NeroNriDetector.SystemAreaRecord? neroSystemAreaRecord = null;
        try
        {
            byte[] sector15 = await image.ReadForm1SectorAsync(SystemAreaSectors - 1, cancellationToken).ConfigureAwait(false);
            NeroNriDetector.TryParseSystemAreaRecord(sector15, out neroSystemAreaRecord);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            // A hidden NRI payload remains useful even when sector 15 is unavailable or
            // uses an unsupported physical sector form.
        }

        if (neroSystemAreaRecord is not null &&
            !mergedPrimaryFiles.Any(file =>
                file.IsEmbeddedNeroProject &&
                file.ExtentLba == neroSystemAreaRecord.ExtentLba &&
                file.DataLength == neroSystemAreaRecord.DataLength &&
                Path.GetFileName(file.Path).Equals(neroSystemAreaRecord.FileName, StringComparison.OrdinalIgnoreCase)) &&
            neroSystemAreaRecord.ExtentLba < image.SectorCount)
        {
            try
            {
                byte[] header = await image.ReadForm1SectorAsync(
                    neroSystemAreaRecord.ExtentLba,
                    cancellationToken).ConfigureAwait(false);
                int headerLength = (int)Math.Min((uint)header.Length, neroSystemAreaRecord.DataLength);
                if (NeroNriDetector.TryReadPayloadSignature(
                        header.AsSpan(0, headerLength),
                        out string signature))
                {
                    mergedPrimaryFiles.Add(new DicDonorFile(
                        neroSystemAreaRecord.FileName,
                        neroSystemAreaRecord.ExtentLba,
                        neroSystemAreaRecord.DataLength,
                        FileFlags: 0x01,
                        IsEmbeddedNeroProject: true,
                        NeroIsoSignature: signature,
                        NeroDirectoryNamespaces: "sector 15"));
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
            {
                // The requirement is retained below and becomes an explicit warning.
            }
        }

        var neroRequirements = primaryNeroRequirements
            .GroupBy(requirement => (
                requirement.FileName.ToUpperInvariant(),
                requirement.ExtentLba,
                requirement.DataLength))
            .Select(group => new NeroNriPayloadRequirement(
                group.First().FileName,
                group.Key.ExtentLba,
                group.Key.DataLength,
                "embedded " + string.Join(
                    "+",
                    group.Select(requirement => requirement.Evidence)
                        .Distinct(StringComparer.OrdinalIgnoreCase)) + " directory record"))
            .ToList();
        if (neroSystemAreaRecord is not null &&
            !neroRequirements.Any(requirement =>
                requirement.FileName.Equals(neroSystemAreaRecord.FileName, StringComparison.OrdinalIgnoreCase) &&
                requirement.ExtentLba == neroSystemAreaRecord.ExtentLba &&
                requirement.DataLength == neroSystemAreaRecord.DataLength))
        {
            neroRequirements.Add(new NeroNriPayloadRequirement(
                neroSystemAreaRecord.FileName,
                neroSystemAreaRecord.ExtentLba,
                neroSystemAreaRecord.DataLength,
                "valid Nero sector-15 record"));
        }

        return new DonorFilesystem(
            pvd,
            volumeId,
            jolietSvd is not null,
            mergedPrimaryFiles,
            combinedJolietFiles,
            metadata,
            neroSystemAreaRecord,
            neroRequirements);
    }


    private static IReadOnlyList<DicDonorFile> CombineMultiExtentDonorFiles(IReadOnlyList<DicDonorFile> files)
    {
        var output = new List<DicDonorFile>(files.Count);
        var consumed = new HashSet<int>();

        for (int i = 0; i < files.Count; i++)
        {
            if (!consumed.Add(i))
                continue;

            DicDonorFile first = files[i];
            if ((first.FileFlags & 0x80) == 0 || first.RequiresExactDonorSemantics)
            {
                output.Add(first);
                continue;
            }

            var chain = new List<DicDonorFile> { first };
            var chainIndexes = new List<int>();
            for (int j = i + 1; j < files.Count; j++)
            {
                DicDonorFile candidate = files[j];
                if (!NormalizePath(candidate.Path).Equals(NormalizePath(first.Path), StringComparison.OrdinalIgnoreCase) ||
                    candidate.IsAssociated != first.IsAssociated)
                    continue;

                chainIndexes.Add(j);
                chain.Add(candidate);
                if ((candidate.FileFlags & 0x80) == 0)
                    break;
            }

            if (chain.Count == 1 || (chain[^1].FileFlags & 0x80) != 0 || chain.Any(item => item.RequiresExactDonorSemantics))
            {
                output.Add(first);
                continue;
            }

            foreach (int chainIndex in chainIndexes)
                consumed.Add(chainIndex);

            long totalLength = chain.Sum(item => item.DataLength);
            var extents = chain.Select(item => new DicDonorExtent(
                item.ExtentLba,
                item.DataLength,
                item.ExtendedAttributeRecordLength,
                item.FileUnitSize,
                item.InterleaveGapSize)).ToArray();
            byte combinedFlags = (byte)(chain[^1].FileFlags & ~0x80);
            output.Add(first with
            {
                DataLength = totalLength,
                FileFlags = combinedFlags,
                Extents = extents
            });
        }

        return output;
    }

    private static async Task CollectDescriptorMetadataAndTreeAsync(
        DonorImageReader image,
        byte[] descriptor,
        bool joliet,
        List<DicDonorFile> files,
        List<NeroNriPayloadRequirement> neroRequirements,
        HashSet<long> metadata,
        CancellationToken cancellationToken)
    {
        uint pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(132, 4));
        long pathSectors = (pathTableSize + CookedSectorSize - 1L) / CookedSectorSize;
        uint littlePath = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(140, 4));
        uint optionalLittlePath = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(144, 4));
        uint bigPath = BinaryPrimitives.ReadUInt32BigEndian(descriptor.AsSpan(148, 4));
        uint optionalBigPath = BinaryPrimitives.ReadUInt32BigEndian(descriptor.AsSpan(152, 4));
        foreach (uint start in new[] { littlePath, optionalLittlePath, bigPath, optionalBigPath }.Where(v => v > 0).Distinct())
        {
            for (long i = 0; i < pathSectors; i++) metadata.Add(start + i);
        }

        if (descriptor.Length < 190 || descriptor[156] < 34)
            return;
        int rootExtendedAttributeLength = descriptor[157];
        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(158, 4));
        uint rootLength = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(166, 4));
        var visited = new HashSet<uint>();
        await ParseDirectoryAsync(
            image,
            checked(rootLba + (uint)rootExtendedAttributeLength),
            rootLength,
            string.Empty,
            joliet,
            files,
            neroRequirements,
            metadata,
            visited,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ParseDirectoryAsync(
        DonorImageReader image,
        uint extentLba,
        uint dataLength,
        string parentPath,
        bool joliet,
        List<DicDonorFile> files,
        List<NeroNriPayloadRequirement> neroRequirements,
        HashSet<long> metadata,
        HashSet<uint> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(extentLba))
            return;

        int sectors = checked((int)((dataLength + CookedSectorSize - 1L) / CookedSectorSize));
        for (int i = 0; i < sectors; i++) metadata.Add(extentLba + i);
        byte[] bytes = await ReadLogicalBytesAsync(image, extentLba, dataLength, cancellationToken).ConfigureAwait(false);

        int position = 0;
        int recordIndex = 0;
        while (position < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int recordLength = bytes[position];
            if (recordLength == 0)
            {
                position = ((position / CookedSectorSize) + 1) * CookedSectorSize;
                continue;
            }
            if (recordLength < 34 || position + recordLength > bytes.Length)
                break;

            if (NeroNriDetector.TryParseEmbeddedDirectoryRecord(
                    bytes.AsSpan(position, recordLength),
                    out NeroNriDetector.EmbeddedDirectoryRecord? embeddedNri) &&
                embeddedNri is not null)
            {
                if (!neroRequirements.Any(requirement =>
                        requirement.FileName.Equals(embeddedNri.FileName, StringComparison.OrdinalIgnoreCase) &&
                        requirement.ExtentLba == embeddedNri.ExtentLba &&
                        requirement.DataLength == embeddedNri.DataLength))
                {
                    neroRequirements.Add(new NeroNriPayloadRequirement(
                        embeddedNri.FileName,
                        embeddedNri.ExtentLba,
                        embeddedNri.DataLength,
                        joliet ? "Joliet" : "ISO9660"));
                }

                if (embeddedNri.ExtentLba < image.SectorCount)
                {
                    try
                    {
                        long payloadLba = checked((long)embeddedNri.ExtentLba + embeddedNri.ExtendedAttributeRecordLength);
                        byte[] header = await image.ReadForm1SectorAsync(payloadLba, cancellationToken).ConfigureAwait(false);
                        int headerLength = (int)Math.Min((uint)header.Length, embeddedNri.DataLength);
                        if (NeroNriDetector.TryReadPayloadSignature(
                                header.AsSpan(0, headerLength),
                                out string neroSignature) &&
                            !files.Any(file => file.IsEmbeddedNeroProject &&
                                file.ExtentLba == embeddedNri.ExtentLba &&
                                file.DataLength == embeddedNri.DataLength &&
                                Path.GetFileName(file.Path).Equals(embeddedNri.FileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            int nestedPosition = position + embeddedNri.RecordOffset;
                            DateTimeOffset? recordingTime = TryReadIsoRecordingTime(
                                bytes.AsSpan(nestedPosition, embeddedNri.RecordLength),
                                out DateTimeOffset parsedTime)
                                ? parsedTime
                                : null;
                            string nriPath = string.IsNullOrEmpty(parentPath)
                                ? embeddedNri.FileName
                                : parentPath + "/" + embeddedNri.FileName;
                            files.Add(new DicDonorFile(
                                nriPath,
                                embeddedNri.ExtentLba,
                                embeddedNri.DataLength,
                                recordingTime,
                                embeddedNri.FileFlags,
                                embeddedNri.ExtendedAttributeRecordLength,
                                embeddedNri.FileUnitSize,
                                embeddedNri.InterleaveGapSize,
                                Extents: null,
                                DirectoryExtentLba: extentLba,
                                DirectoryRecordOffset: nestedPosition,
                                DirectoryRecordIndex: recordIndex,
                                DirectoryRecordLength: embeddedNri.RecordLength,
                                IdentifierLength: embeddedNri.IdentifierLength,
                                IdentifierPaddingValue: TryReadIdentifierPadding(
                                    bytes,
                                    nestedPosition,
                                    embeddedNri.RecordLength,
                                    embeddedNri.IdentifierLength),
                                IsEmbeddedNeroProject: true,
                                NeroIsoSignature: neroSignature,
                                NeroDirectoryNamespaces: joliet ? "Joliet" : "ISO9660"));
                        }
                    }
                    catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException or OverflowException)
                    {
                        // Treat an unreadable or missing payload as an unsatisfied NRI
                        // requirement; it must never break extraction of the visible tree.
                    }
                }
            }

            int idLength = bytes[position + 32];
            if (33 + idLength <= recordLength && idLength > 0)
            {
                int idOffset = position + 33;
                bool dot = idLength == 1 && (bytes[idOffset] == 0 || bytes[idOffset] == 1);
                if (!dot)
                {
                    byte[] identifierBytes = new byte[idLength];
                    Buffer.BlockCopy(bytes, idOffset, identifierBytes, 0, idLength);
                    string name = DecodeIdentifier(identifierBytes, joliet);
                    name = StripIsoVersion(name);
                    uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 2, 4));
                    uint childLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 10, 4));
                    byte fileFlags = bytes[position + 25];
                    bool isDirectory = (fileFlags & 0x02) != 0;
                    string childPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
                    DateTimeOffset? recordingTime = TryReadIsoRecordingTime(bytes.AsSpan(position, recordLength), out DateTimeOffset parsedTime)
                        ? parsedTime
                        : null;
                    int extendedAttributeRecordLength = bytes[position + 1];
                    int fileUnitSize = bytes[position + 26];
                    int interleaveGapSize = bytes[position + 27];
                    byte? identifierPaddingValue = TryReadIdentifierPadding(
                        bytes,
                        position,
                        recordLength,
                        idLength);

                    files.Add(new DicDonorFile(
                        childPath,
                        childLba,
                        childLength,
                        recordingTime,
                        fileFlags,
                        extendedAttributeRecordLength,
                        fileUnitSize,
                        interleaveGapSize,
                        Extents: null,
                        DirectoryExtentLba: extentLba,
                        DirectoryRecordOffset: position,
                        DirectoryRecordIndex: recordIndex,
                        DirectoryRecordLength: recordLength,
                        IdentifierLength: idLength,
                        IdentifierPaddingValue: identifierPaddingValue));

                    if (isDirectory)
                    {
                        uint directoryDataLba = checked(childLba + (uint)extendedAttributeRecordLength);
                        await ParseDirectoryAsync(
                            image,
                            directoryDataLba,
                            childLength,
                            childPath,
                            joliet,
                            files,
                            neroRequirements,
                            metadata,
                            visited,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            position += recordLength;
            recordIndex++;
        }
    }

    internal static byte? TryReadIdentifierPadding(
        byte[] directoryBytes,
        int recordOffset,
        int recordLength,
        int identifierLength)
    {
        if ((identifierLength & 1) != 0)
            return null;

        int paddingOffset = recordOffset + 33 + identifierLength;
        int recordEnd = recordOffset + recordLength;
        if (recordOffset < 0 || recordLength < 34 || identifierLength <= 0 ||
            paddingOffset < 0 || paddingOffset >= recordEnd || paddingOffset >= directoryBytes.Length)
            return null;

        return directoryBytes[paddingOffset];
    }

    private static async Task<byte[]> ReadLogicalBytesAsync(
        DonorImageReader image,
        uint startLba,
        uint dataLength,
        CancellationToken cancellationToken)
    {
        byte[] result = new byte[checked((int)dataLength)];
        int written = 0;
        long lba = startLba;
        while (written < result.Length)
        {
            byte[] sector = await image.ReadForm1SectorAsync(lba++, cancellationToken).ConfigureAwait(false);
            int count = Math.Min(sector.Length, result.Length - written);
            Buffer.BlockCopy(sector, 0, result, written, count);
            written += count;
        }
        return result;
    }

    private static bool TryReadIsoRecordingTime(ReadOnlySpan<byte> record, out DateTimeOffset value)
    {
        value = default;
        if (record.Length < 25)
            return false;

        try
        {
            int year = 1900 + record[18];
            int month = record[19];
            int day = record[20];
            int hour = record[21];
            int minute = record[22];
            int second = record[23];
            sbyte quarterHours = unchecked((sbyte)record[24]);
            if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59 || quarterHours is < -48 or > 52)
                return false;

            TimeSpan offset = TimeSpan.FromMinutes(quarterHours * 15);
            value = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsJolietDescriptor(byte[] sector)
        => sector.Length >= 91 && sector[88] == 0x25 && sector[89] == 0x2F &&
           (sector[90] == 0x40 || sector[90] == 0x43 || sector[90] == 0x45);

    private static string DecodeIdentifier(ReadOnlySpan<byte> bytes, bool joliet)
        => joliet
            ? Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0')
            : Encoding.ASCII.GetString(bytes).TrimEnd('\0');

    private static string StripIsoVersion(string value)
    {
        int semicolon = value.LastIndexOf(';');
        return semicolon > 0 && value[(semicolon + 1)..].All(char.IsDigit)
            ? value[..semicolon]
            : value;
    }

}
