using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    public static bool CanRecoverKnownGap(
        SkeletonInspectionResult inspection,
        SkeletonContentEntry entry)
    {
        if (entry.SpecialKind != SkeletonSpecialKind.Gap)
            return false;

        return inspection.KnownGapRecoveries.Any(info =>
            info.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(info.ExpectedSha1, entry.Sha1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(info.ExpectedXaSha1, entry.XaSha1, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<SkeletonSourceMatch> RecoverKnownGaps(SkeletonInspectionResult inspection)
    {
        var matches = new List<SkeletonSourceMatch>();
        foreach (KnownGapRecoveryInfo info in inspection.KnownGapRecoveries)
        {
            SkeletonContentEntry? entry = inspection.Entries.FirstOrDefault(candidate =>
                candidate.SpecialKind == SkeletonSpecialKind.Gap &&
                candidate.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Sha1, info.ExpectedSha1, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.XaSha1, info.ExpectedXaSha1, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;

            bool generatedPayload = info.GeneratedPayload is not null;
            matches.Add(new SkeletonSourceMatch(
                entry,
                $"Generated known GAP: {info.PatternName}",
                info.ExpectedSha1,
                IsXa: false,
                MatchMethod: generatedPayload
                    ? "Known Toast HFS GAP reconstruction"
                    : "Known Redumper GAP reconstruction",
                SourceRelativePath: info.PatternName,
                SourceLength: info.GeneratedPayload?.LongLength ?? 0,
                GeneratedPayload: info.GeneratedPayload,
                GeneratedGapRecovery: generatedPayload ? null : info));
        }
        return matches;
    }

    private static async Task<IReadOnlyList<KnownGapRecoveryInfo>> TryCreateKnownGapRecoveriesAsync(
        SkeletonImageReader image,
        IReadOnlyList<SkeletonContentEntry> entries,
        CancellationToken cancellationToken)
    {
        if (image.Kind != SkeletonImageKind.Raw2352)
            return Array.Empty<KnownGapRecoveryInfo>();

        var recoveries = new List<KnownGapRecoveryInfo>();
        byte[] zeroForm1 = new byte[CookedSectorSize];
        byte[] fill55Form1 = new byte[CookedSectorSize];
        Array.Fill(fill55Form1, (byte)0x55);
        byte[] zeroForm2 = new byte[2324];
        byte[] fill55Form2 = new byte[2324];
        Array.Fill(fill55Form2, (byte)0x55);

        foreach (SkeletonContentEntry entry in entries.Where(candidate =>
                     candidate.SpecialKind == SkeletonSpecialKind.Gap &&
                     candidate.CanRestore &&
                     candidate.DataLength > 0 &&
                     candidate.DataLength % CookedSectorSize == 0 &&
                     (!string.IsNullOrWhiteSpace(candidate.Sha1) ||
                      !string.IsNullOrWhiteSpace(candidate.XaSha1))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sectorCount = entry.DataLength / CookedSectorSize;
            long startIndex = (long)entry.ExtentLba - image.BaseLba;
            if (startIndex < 0 || sectorCount <= 0 || startIndex + sectorCount > image.SectorCount)
                continue;

            using IncrementalHash form1Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using IncrementalHash form2Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var generatedFill55Lbas = new HashSet<long>();
            long form1Sectors = 0;
            long form2Sectors = 0;
            long preservedSectors = 0;

            for (long sectorOffset = 0; sectorOffset < sectorCount; sectorOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long lba = checked((long)entry.ExtentLba + sectorOffset);
                byte[] sector = await image.ReadRawSectorAsync(lba, cancellationToken).ConfigureAwait(false);

                // Redumper's BIN reader requires an exact sync and exact mode byte.
                // Unknown/invalid sectors are omitted from the manifest hash and the
                // skeleton creator leaves their complete raw bytes untouched.
                if (!sector.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern) ||
                    sector[15] is not (1 or 2))
                {
                    preservedSectors++;
                    continue;
                }

                bool fill55 = IsRedumperGeneratedFill55SkeletonSector(sector);
                if (fill55)
                    generatedFill55Lbas.Add(lba);

                if (sector[15] == 1 || (sector[18] & XaForm2Bit) == 0)
                {
                    form1Hash.AppendData(fill55 ? fill55Form1 : zeroForm1);
                    form1Sectors++;
                }
                else
                {
                    form2Hash.AppendData(fill55 ? fill55Form2 : zeroForm2);
                    form2Sectors++;
                }
            }

            string actualSha1 = Convert.ToHexString(form1Hash.GetHashAndReset()).ToLowerInvariant();
            string actualXaSha1 = Convert.ToHexString(form2Hash.GetHashAndReset()).ToLowerInvariant();
            // Never generate a sector form whose logical payload is not covered by
            // the manifest. Hash agreement must prove every erased byte we restore.
            if ((form1Sectors > 0 && string.IsNullOrWhiteSpace(entry.Sha1)) ||
                (form2Sectors > 0 && string.IsNullOrWhiteSpace(entry.XaSha1)))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entry.Sha1) &&
                !actualSha1.Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entry.XaSha1) &&
                !actualXaSha1.Equals(entry.XaSha1, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string pattern =
                $"Redumper zero/0x55 pattern: {form1Sectors:N0} Form 1, {form2Sectors:N0} Form 2, " +
                $"{generatedFill55Lbas.Count:N0} generated 0x55, {preservedSectors:N0} preserved invalid";
            recoveries.Add(new KnownGapRecoveryInfo(
                entry.Path,
                pattern,
                entry.Sha1 ?? actualSha1,
                entry.XaSha1,
                entry.ExtentLba,
                sectorCount,
                form1Sectors,
                form2Sectors,
                preservedSectors,
                generatedFill55Lbas));
        }

        return recoveries;
    }

    private static bool IsRedumperGeneratedFill55SkeletonSector(ReadOnlySpan<byte> sector)
    {
        if (sector[15] == 1)
        {
            // Redumper clears Mode 1 user data, EDC and ECC, but not the eight-byte
            // reserved field. A generated sector therefore retains eight 0x55 bytes.
            return IsFilledWith(sector.Slice(2068, 8), 0x55);
        }

        // Redumper fills bytes 16..2351 when it generates a data sector. Skeleton
        // erasure preserves the XA subheader at bytes 16..23, providing the marker.
        return sector[15] == 2 && IsFilledWith(sector.Slice(16, 8), 0x55);
    }

    private static bool IsFilledWith(ReadOnlySpan<byte> bytes, byte value)
    {
        foreach (byte item in bytes)
        {
            if (item != value)
                return false;
        }
        return true;
    }
}
