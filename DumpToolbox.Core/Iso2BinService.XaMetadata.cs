using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class Iso2BinService
{
    private static async Task<XaMetadataMap> LoadXaMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadataPath))
            throw new ArgumentException("Choose a DIC *_EccEdc.txt file or a raw Redumper .skeleton file.", nameof(metadataPath));

        string fullPath = Path.GetFullPath(metadataPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("XA metadata source not found.", fullPath);

        if (Path.GetExtension(fullPath).Equals(".skeleton", StringComparison.OrdinalIgnoreCase))
            return await LoadSkeletonXaMetadataAsync(fullPath, cancellationToken);

        return await Task.Run(() => LoadDicXaMetadata(fullPath, cancellationToken), cancellationToken);
    }

    private static XaMetadataMap LoadDicXaMetadata(string path, CancellationToken cancellationToken)
    {
        var sectors = new Dictionary<long, XaSectorMetadata>();
        int form1 = 0;
        int form2 = 0;
        long first = long.MaxValue;
        long last = long.MinValue;

        foreach (string line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match lbaMatch = DicEccLbaRegex.Match(line);
            Match modeMatch = DicEccModeRegex.Match(line);
            if (!lbaMatch.Success || !modeMatch.Success)
                continue;

            long lba = long.Parse(lbaMatch.Groups["lba"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int mode = int.Parse(modeMatch.Groups["mode"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int form = mode == 2 && modeMatch.Groups["form"].Success
                ? int.Parse(modeMatch.Groups["form"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1;

            byte fileNumber = ParseDicHexByte(DicFileNumberRegex.Match(line));
            byte channelNumber = ParseDicHexByte(DicChannelNumberRegex.Match(line));
            byte submode = ParseDicHexByte(DicSubmodeRegex.Match(line), mode == 2 ? (byte)(form == 2 ? 0x28 : 0x08) : (byte)0);
            byte codingInfo = ParseDicHexByte(DicCodingInfoRegex.Match(line));

            sectors[lba] = new XaSectorMetadata(mode, form, fileNumber, channelNumber, submode, codingInfo);
            first = Math.Min(first, lba);
            last = Math.Max(last, lba);
            if (mode == 2 && form == 1) form1++;
            if (mode == 2 && form == 2) form2++;
        }

        if (sectors.Count == 0)
            throw new InvalidOperationException("No DIC EccEdc sector records were found in the selected text file.");

        var inspection = new XaMetadataInspection(
            path,
            XaMetadataSourceKind.DiscImageCreatorEccEdc,
            first,
            last,
            sectors.Count,
            form1,
            form2,
            $"DIC XA metadata: {sectors.Count:N0} sector records, LBA {first:N0}–{last:N0}; {form1:N0} Mode 2 Form 1 and {form2:N0} Mode 2 Form 2 record(s).");

        return new XaMetadataMap(sectors, inspection);
    }

    private static async Task<XaMetadataMap> LoadSkeletonXaMetadataAsync(string path, CancellationToken cancellationToken)
    {
        long length = new FileInfo(path).Length;
        if (length <= 0 || length % RawSectorSize != 0)
            throw new InvalidOperationException("The Redumper skeleton must be a raw 2352-byte/sector skeleton to provide XA subheaders.");

        long sectorCount = length / RawSectorSize;
        var sectors = new Dictionary<long, XaSectorMetadata>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(RawSectorSize * BatchSectors);
        int form1 = 0;
        int form2 = 0;
        long baseLba;

        try
        {
            await using var input = OpenRead(path, FileOptions.Asynchronous | FileOptions.SequentialScan, RawSectorSize * BatchSectors);
            await ReadExactlyAsync(input, buffer.AsMemory(0, RawSectorSize), cancellationToken);
            baseLba = ValidateAndDecodeSkeletonBaseLba(buffer);
            input.Position = 0;
            long processed = 0;

            while (processed < sectorCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BatchSectors, sectorCount - processed);
                int bytes = count * RawSectorSize;
                await ReadExactlyAsync(input, buffer.AsMemory(0, bytes), cancellationToken);

                ProcessSkeletonMetadataBatch(buffer, count, processed, baseLba, sectors, ref form1, ref form2);
                processed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        long lastLba = baseLba + sectorCount - 1;
        var inspection = new XaMetadataInspection(
            path,
            XaMetadataSourceKind.RedumperSkeleton,
            baseLba,
            lastLba,
            sectors.Count,
            form1,
            form2,
            $"Redumper raw skeleton XA metadata: {sectorCount:N0} sectors, LBA {baseLba:N0}–{lastLba:N0}; {form1:N0} Mode 2 Form 1 and {form2:N0} Mode 2 Form 2 sector(s).");

        return new XaMetadataMap(sectors, inspection);
    }

    private static byte ParseDicHexByte(Match match, byte fallback = 0)
    {
        if (!match.Success)
            return fallback;
        return byte.TryParse(
            match.Groups["v"].Value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out byte value)
            ? value
            : fallback;
    }

    private static long ValidateAndDecodeSkeletonBaseLba(byte[] buffer)
    {
        ReadOnlySpan<byte> sector = buffer.AsSpan(0, RawSectorSize);
        if (!sector.Slice(0, SyncPattern.Length).SequenceEqual(SyncPattern))
            throw new InvalidOperationException("The selected .skeleton is not a sync-aligned raw 2352-byte CD skeleton.");
        return DecodeRawHeaderLba(sector);
    }

    private static void ProcessSkeletonMetadataBatch(
        byte[] buffer,
        int count,
        long processed,
        long baseLba,
        Dictionary<long, XaSectorMetadata> sectors,
        ref int form1,
        ref int form2)
    {
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> sector = buffer.AsSpan(i * RawSectorSize, RawSectorSize);
            if (!sector.Slice(0, SyncPattern.Length).SequenceEqual(SyncPattern))
                throw new InvalidOperationException($"Invalid raw sector sync in Redumper skeleton at sector index {processed + i:N0}.");

            long lba = baseLba + processed + i;
            int mode = sector[15];
            if (mode == 2)
            {
                int form = (sector[18] & 0x20) != 0 ? 2 : 1;
                sectors[lba] = new XaSectorMetadata(mode, form, sector[16], sector[17], sector[18], sector[19]);
                if (form == 1) form1++; else form2++;
            }
            else if (mode == 1)
            {
                sectors[lba] = new XaSectorMetadata(1, 1, 0, 0, 0, 0);
            }
        }
    }

    private static long DecodeRawHeaderLba(ReadOnlySpan<byte> sector)
    {
        int minute = DecodeBcdByte(sector[12]);
        int second = DecodeBcdByte(sector[13]);
        int frame = DecodeBcdByte(sector[14]);
        return checked((minute * 60L + second) * 75L + frame - 150L);
    }

    private static int DecodeBcdByte(byte value)
    {
        int high = (value >> 4) & 0x0F;
        int low = value & 0x0F;
        if (high > 9 || low > 9)
            throw new InvalidOperationException($"Invalid BCD sector address byte 0x{value:X2} in raw skeleton.");
        return high * 10 + low;
    }

    private static void ReportXaUsage(IProgress<string>? activity, XaMetadataMap? metadata, XaMetadataUsage usage)
    {
        if (metadata is null)
            return;

        activity?.Report(
            $"XA metadata usage: {usage.ExactSubheaders:N0} exact Mode 2 Form 1 subheader(s); " +
            $"{usage.GenericSubheaders:N0} Mode 2 Form 1 sector(s) used the generic fallback.");
    }
}
