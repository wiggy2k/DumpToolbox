using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    internal static NeroSystemAreaRecoveryInfo? DetectNeroProjectFromMainInfoSectors(
        IReadOnlyDictionary<long, byte[]> sectors)
    {
        var candidates = new Dictionary<(string Name, uint Lba, uint Length), string>();

        foreach (byte[] sector in sectors.Values)
        {
            if (sector.Length != CookedSectorSize)
                continue;

            AddNeroCandidates(sector, written: null, sectors, candidates);
        }

        return CreateNeroRecoveryInfo(candidates);
    }

    internal static NeroSystemAreaRecoveryInfo? DetectNeroProjectFromMainInfoLog(
        string path,
        IReadOnlyDictionary<long, byte[]> completeSectors,
        CancellationToken cancellationToken = default)
    {
        var candidates = new Dictionary<(string Name, uint Lba, uint Length), string>();
        Dictionary<long, MetadataBuffer>? currentDump = null;
        long? dumpBaseLba = null;

        void FlushDump()
        {
            if (currentDump is not null)
            {
                foreach (MetadataBuffer buffer in currentDump.Values)
                    AddNeroCandidates(buffer.Data, buffer.Written, completeSectors, candidates);
            }

            currentDump = null;
            dumpBaseLba = null;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = rawLine.TrimEnd();

            if (line.StartsWith("========== OpCode", StringComparison.OrdinalIgnoreCase))
            {
                FlushDump();
                continue;
            }

            Match heading = LbaHeadingRegex.Match(line);
            if (heading.Success)
            {
                FlushDump();
                string kind = heading.Groups["kind"].Value;
                long parsedLba = long.Parse(heading.Groups["lba"].Value, CultureInfo.InvariantCulture);
                // Some older DIC builds never emit an OpCode boundary after their
                // initial drive-offset checks. LBA >= 16 plus a fully valid mirrored
                // ISO directory record is the reliable discriminator here; retaining
                // an "in offset check" flag would discard every later filesystem dump.
                if (parsedLba >= 16 &&
                    kind.Contains("Main Channel", StringComparison.OrdinalIgnoreCase))
                {
                    dumpBaseLba = parsedLba;
                    currentDump = new Dictionary<long, MetadataBuffer>();
                }
                continue;
            }

            if (dumpBaseLba is null || currentDump is null)
                continue;

            Match hexLine = MainHexLineRegex.Match(line);
            if (!hexLine.Success)
                continue;

            int offset = int.Parse(hexLine.Groups["ofs"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string[] tokens = hexLine.Groups["bytes"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int byteCount = 0;
            foreach (string token in tokens)
            {
                if (byteCount >= 16 || token.Length != 2 ||
                    !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                {
                    break;
                }

                long absoluteOffset = (long)offset + byteCount;
                long lba = dumpBaseLba.Value + absoluteOffset / CookedSectorSize;
                int within = (int)(absoluteOffset % CookedSectorSize);
                if (!currentDump.TryGetValue(lba, out MetadataBuffer? target))
                {
                    target = new MetadataBuffer();
                    currentDump[lba] = target;
                }
                target.Data[within] = value;
                target.Written[within] = true;
                byteCount++;
            }
        }

        FlushDump();
        return CreateNeroRecoveryInfo(candidates);
    }

    private static void AddNeroCandidates(
        byte[] sector,
        bool[]? written,
        IReadOnlyDictionary<long, byte[]> completeSectors,
        Dictionary<(string Name, uint Lba, uint Length), string> candidates)
    {
        // ISO9660 directory records may stop before the end of the sector. Search
        // every plausible record boundary so this also works with older DIC logs
        // that omit the trailing all-zero portion of a directory sector.
        for (int offset = 0; offset + 34 <= sector.Length; offset++)
        {
            if (written is not null && !written[offset])
                continue;

            int recordLength = sector[offset];
            if (recordLength < 34 || offset + recordLength > sector.Length)
                continue;
            if (written is not null && !AllBytesWritten(written, offset, recordLength))
                continue;

            if (!SkeletonResurrectionService.TryParseEmbeddedNeroProjectDirectoryRecord(
                    sector.AsSpan(offset, recordLength),
                    out string fileName,
                    out uint extentLba,
                    out uint dataLength))
            {
                continue;
            }

            string signature = TryReadNeroSignature(completeSectors, extentLba)
                ?? "NeroISO signature not present in DIC logs";
            candidates[(fileName, extentLba, dataLength)] = signature;
        }
    }

    private static bool AllBytesWritten(bool[] written, int offset, int length)
    {
        for (int i = offset; i < offset + length; i++)
        {
            if (!written[i])
                return false;
        }
        return true;
    }

    private static NeroSystemAreaRecoveryInfo? CreateNeroRecoveryInfo(
        Dictionary<(string Name, uint Lba, uint Length), string> candidates)
    {
        // Primary ISO9660 and Joliet normally contain equivalent copies of the hidden
        // record. Multiple genuinely different NRI records are ambiguous and must not
        // select a system-area recipe automatically.
        if (candidates.Count != 1)
            return null;

        KeyValuePair<(string Name, uint Lba, uint Length), string> candidate = candidates.Single();
        return new NeroSystemAreaRecoveryInfo(
            candidate.Key.Name,
            candidate.Key.Lba,
            candidate.Key.Length,
            candidate.Value,
            string.Empty)
        {
            DetectedFromDic = true
        };
    }

    private static string? TryReadNeroSignature(
        IReadOnlyDictionary<long, byte[]> sectors,
        uint extentLba)
    {
        if (!sectors.TryGetValue(extentLba, out byte[]? payload) || payload.Length < 8)
            return null;

        int length = payload[0];
        if (length < 7 || length > payload.Length - 1 ||
            !payload.AsSpan(1, 7).SequenceEqual("NeroISO"u8))
        {
            return null;
        }

        return Encoding.ASCII.GetString(payload, 1, length);
    }

    private static NeroSystemAreaRecoveryInfo ApplyDicNeroSystemAreaEvidence(
        NeroSystemAreaRecoveryInfo info,
        IReadOnlyDictionary<long, DicPayloadEvidence> offsetEvidence)
    {
        byte[] template = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(info, 0);
        bool compatible = true;

        for (long lba = 0; lba < 16 && compatible; lba++)
        {
            if (!offsetEvidence.TryGetValue(lba, out DicPayloadEvidence? evidence))
                continue;

            int templateOffset = checked((int)lba * CookedSectorSize);
            for (int i = 0; i < CookedSectorSize; i++)
            {
                if (!evidence.Known[i])
                    continue;

                int absolute = templateOffset + i;
                if (absolute >= 15 * CookedSectorSize + 24 && absolute < 15 * CookedSectorSize + 28)
                    continue;

                if (evidence.Data[i] != template[absolute])
                {
                    compatible = false;
                    break;
                }
            }
        }

        uint? knownPrivateValue = null;
        if (compatible &&
            offsetEvidence.TryGetValue(15, out DicPayloadEvidence? finalSector) &&
            finalSector.Known.AsSpan(24, 4).ToArray().All(value => value))
        {
            knownPrivateValue = BinaryPrimitives.ReadUInt32BigEndian(finalSector.Data.AsSpan(24, 4));
        }

        return info with
        {
            DicSystemAreaLayoutCompatible = compatible,
            DicKnownPrivateValue = knownPrivateValue
        };
    }
}
