using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

/// <summary>
/// CUE-directed boundary recovery for mixed-mode discs whose Track 02 pregap
/// differs from the normal 00:02:00 by one or more raw sectors.
///
/// These tests are deliberately narrow and hash-gated:
///  * pregap shorter than 150 frames: retry Track 01 with the corresponding
///    number of final raw sectors replaced by zeroes;
///  * pregap longer than 150 frames: after ordinary zero-silence recovery has
///    failed for a Track 02 that is proven short at its beginning by Track 03,
///    synthesize the corresponding empty data sector(s) at the MSF address(es)
///    immediately after Track 01 ends, scramble them, then add enough zero bytes
///    to satisfy the remaining exact shortfall.
///
/// No candidate is accepted unless the supplied CRC32 and MD5 (when present)
/// verify exactly.
/// </summary>
public sealed class PregapLengthQuirkRecoveryService
{
    private const int SectorSize = 2352;
    private const int NormalTrack2PregapFrames = 150;
    private const int BufferSize = 4 * 1024 * 1024;

    private static readonly byte[] Sync =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    public async Task<SearchResult?> TryRepairTrack1ForShortPregapAsync(
        string sourceFile,
        HashTarget track1Target,
        int track1TargetIndex,
        int track2PregapFrames,
        SearchResult currentTrack1Result,
        string outputDirectory,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        if (currentTrack1Result.Found || track2PregapFrames >= NormalTrack2PregapFrames)
            return null;

        int zeroSectors = NormalTrack2PregapFrames - track2PregapFrames;
        long zeroBytes = checked((long)zeroSectors * SectorSize);
        if (zeroSectors <= 0 || track1Target.Size < zeroBytes || track1Target.Size % SectorSize != 0)
            return null;

        long sourceLength = new FileInfo(sourceFile).Length;
        if (sourceLength < track1Target.Size)
            return null;

        Directory.CreateDirectory(outputDirectory);
        string temp = Path.Combine(outputDirectory, $".dumptoolbox_pregap_t1_{Guid.NewGuid():N}.tmp");
        string finalOutput = GetRecoveredOutputPath(sourceFile, track1Target, track1TargetIndex, outputDirectory);

        try
        {
            activity?.Report(
                $"PREGAP LENGTH: Track 02 pregap is {FormatFrames(track2PregapFrames)} ({zeroSectors:N0} sector(s) shorter than 00:02:00). " +
                $"{TargetDisplayName(track1Target, track1TargetIndex)} is unmatched; retrying with its final {zeroSectors:N0} raw sector(s) replaced by zeroes.");

            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                await CopyRangeAsync(sourceFile, 0, track1Target.Size - zeroBytes, output, cancellationToken).ConfigureAwait(false);
                await WriteZerosAsync(output, zeroBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!VerifyFile(temp, track1Target, cancellationToken, out string md5))
            {
                activity?.Report(
                    $"PREGAP LENGTH: Track 01 zero-tail retry did not match (MD5 {(string.IsNullOrEmpty(md5) ? "n/a" : md5)}).");
                return null;
            }

            File.Move(temp, finalOutput, true);
            string status =
                $"PREGAP LENGTH FIXED: {TargetDisplayName(track1Target, track1TargetIndex)} matched after replacing the final {zeroSectors:N0} raw sector(s) with zeroes; CRC32/MD5 verified.";
            activity?.Report(status);
            return new SearchResult(track1Target, 0, true, status, OutputPath: finalOutput);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public async Task<SearchResult?> TryRepairTrack2ForLongPregapAsync(
        string sourceFile,
        HashTarget track1Target,
        HashTarget track2Target,
        int track2TargetIndex,
        SearchResult track1Result,
        int track2PregapFrames,
        SearchResult currentTrack2Result,
        SearchResult track3Result,
        string outputDirectory,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        if (currentTrack2Result.Found || track2PregapFrames <= NormalTrack2PregapFrames)
            return null;
        if (!track3Result.Found || track3Result.Offset is not long track3Offset)
            return null;

        int scrambledSectors = track2PregapFrames - NormalTrack2PregapFrames;
        long scrambledBytes = checked((long)scrambledSectors * SectorSize);
        if (scrambledSectors <= 0 || track1Target.Size < SectorSize || track1Target.Size % SectorSize != 0)
            return null;

        // Prefer an ordinary verified Track 01 position when available. If
        // Track 01 itself is unmatched, mixed-mode FindCRCs source images are
        // expected to begin with Track 01 at byte zero.
        long track1Start = track1Result.Found && track1Result.Offset is long track1Offset
            ? track1Offset
            : 0;
        // Track 03 is the authoritative upper boundary for a short Track 02.
        long partialStart = checked(track1Start + track1Target.Size);
        long partialLength = track3Offset - partialStart;
        if (partialLength < 0 || partialLength >= track2Target.Size)
            return null;

        long missingBytes = track2Target.Size - partialLength;
        if (missingBytes < scrambledBytes)
        {
            activity?.Report(
                $"PREGAP LENGTH: Track 02 is short by {missingBytes:N0} byte(s), less than the {scrambledBytes:N0} byte(s) required by its {FormatFrames(track2PregapFrames)} pregap; special retry skipped.");
            return null;
        }

        long sourceLength = new FileInfo(sourceFile).Length;
        if (track1Start < 0 || track3Offset > sourceLength || partialStart > sourceLength ||
            track1Start + track1Target.Size > sourceLength)
            return null;

        Directory.CreateDirectory(outputDirectory);
        string temp = Path.Combine(outputDirectory, $".dumptoolbox_pregap_t2_{Guid.NewGuid():N}.tmp");
        string finalOutput = GetRecoveredOutputPath(sourceFile, track2Target, track2TargetIndex, outputDirectory);
        byte[] sector = new byte[SectorSize];

        try
        {
            activity?.Report(
                $"PREGAP LENGTH: Track 02 pregap is {FormatFrames(track2PregapFrames)} ({scrambledSectors:N0} sector(s) longer than 00:02:00). " +
                $"Matched Track 03 proves Track 02 is short at its beginning by {missingBytes:N0} byte(s); ordinary zero-silence recovery did not match. " +
                $"Retrying with {scrambledSectors:N0} synthesized scrambled data sector(s) at the next MSF address(es) after Track 01 ends, " +
                $"followed by {missingBytes - scrambledBytes:N0} zero byte(s).");

            await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.RandomAccess);

            // Read only Track 01's final represented sector to learn its raw data
            // mode and absolute MSF.  The extra pregap sector(s) are NOT copies of
            // this sector: they are newly-built empty data sectors whose MSFs are
            // the immediately following addresses (end MSF + 1, +2, ...).
            long finalTrack1SectorOffset = checked(track1Start + track1Target.Size - SectorSize);
            input.Position = finalTrack1SectorOffset;
            await ReadExactlyAsync(input, sector, cancellationToken).ConfigureAwait(false);
            if (!LooksLikeRawDataSector(sector))
            {
                activity?.Report(
                    $"PREGAP LENGTH: final Track 01 sector at byte {finalTrack1SectorOffset:N0} does not look like a raw Mode 1/2 data sector; special Track 02 retry skipped.");
                return null;
            }

            if (!TryDecodeSectorLba(sector, out long finalTrack1Lba))
            {
                activity?.Report(
                    $"PREGAP LENGTH: final Track 01 sector at byte {finalTrack1SectorOffset:N0} has an invalid BCD MSF; special Track 02 retry skipped.");
                return null;
            }

            byte track1Mode = sector[15];
            byte fileNumber = 0;
            byte channelNumber = 0;
            byte submode = 0x08; // XA data, Form 1 unless the source says Form 2.
            byte codingInfo = 0;
            bool form2 = false;
            if (track1Mode == 0x02)
            {
                fileNumber = sector[16];
                channelNumber = sector[17];
                submode = sector[18];
                codingInfo = sector[19];
                form2 = (submode & 0x20) != 0;
            }

            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.SequentialScan))
            {
                for (int i = 0; i < scrambledSectors; i++)
                {
                    long syntheticLba = checked(finalTrack1Lba + 1 + i);
                    sector.AsSpan().Clear();
                    if (track1Mode == 0x01)
                    {
                        SkeletonResurrectionService.BuildMode1Sector(
                            syntheticLba,
                            new byte[2048],
                            sector);
                    }
                    else if (!form2)
                    {
                        SkeletonResurrectionService.BuildMode2Form1Sector(
                            syntheticLba,
                            new byte[2048],
                            fileNumber,
                            channelNumber,
                            submode,
                            codingInfo,
                            sector);
                    }
                    else
                    {
                        SkeletonResurrectionService.BuildMode2Form2Sector(
                            syntheticLba,
                            new byte[2324],
                            fileNumber,
                            channelNumber,
                            submode,
                            codingInfo,
                            sector);
                    }

                    string msf = FormatAbsoluteMsf(syntheticLba);
                    activity?.Report(
                        $"PREGAP LENGTH: synthesizing empty Track 01 data continuation sector at LBA {syntheticLba:N0} / MSF {msf}, then applying CD scrambling.");
                    CdPregapScrambleService.ScrambleSectorInPlace(sector);
                    await output.WriteAsync(sector, cancellationToken).ConfigureAwait(false);
                }

                await WriteZerosAsync(output, missingBytes - scrambledBytes, cancellationToken).ConfigureAwait(false);
                input.Position = partialStart;
                await CopyExactlyAsync(input, output, partialLength, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Hashing reopens the temp file, so the writer must be fully disposed first.
            if (!VerifyFile(temp, track2Target, cancellationToken, out string md5))
            {
                activity?.Report(
                    $"PREGAP LENGTH: Track 02 scrambled-data + zero-fill retry did not match (MD5 {(string.IsNullOrEmpty(md5) ? "n/a" : md5)}).");
                return null;
            }

            File.Move(temp, finalOutput, true);
            long expectedStart = track3Offset - track2Target.Size;
            string status =
                $"PREGAP LENGTH FIXED: {TargetDisplayName(track2Target, track2TargetIndex)} matched with {scrambledSectors:N0} synthesized scrambled post-Track-01 data sector(s) + {missingBytes - scrambledBytes:N0} zero byte(s) before the available audio; CRC32/MD5 verified.";
            activity?.Report(status);
            return new SearchResult(track2Target, expectedStart, true, status, OutputPath: finalOutput);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static bool LooksLikeRawDataSector(ReadOnlySpan<byte> sector) =>
        sector.Length == SectorSize &&
        sector[..12].SequenceEqual(Sync) &&
        sector[15] is 0x01 or 0x02;


    private static bool TryDecodeSectorLba(ReadOnlySpan<byte> sector, out long lba)
    {
        lba = 0;
        if (sector.Length < SectorSize || !LooksLikeRawDataSector(sector))
            return false;
        if (!TryDecodeBcd(sector[12], out int minute) ||
            !TryDecodeBcd(sector[13], out int second) ||
            !TryDecodeBcd(sector[14], out int frame) ||
            second >= 60 || frame >= 75)
            return false;

        long absoluteFrames = ((long)minute * 60 + second) * 75 + frame;
        lba = absoluteFrames - 150;
        return true;
    }

    private static bool TryDecodeBcd(byte value, out int decoded)
    {
        int high = (value >> 4) & 0x0F;
        int low = value & 0x0F;
        if (high > 9 || low > 9)
        {
            decoded = 0;
            return false;
        }
        decoded = high * 10 + low;
        return true;
    }

    private static string FormatAbsoluteMsf(long lba)
    {
        long absolute = checked(lba + 150);
        long minute = absolute / (60 * 75);
        long second = (absolute / 75) % 60;
        long frame = absolute % 75;
        return $"{minute:00}:{second:00}:{frame:00}";
    }

    private static string FormatFrames(int frames)
    {
        int minutes = frames / (60 * 75);
        int rest = frames % (60 * 75);
        int seconds = rest / 75;
        int ff = rest % 75;
        return $"{minutes:00}:{seconds:00}:{ff:00}";
    }

    private static async Task CopyRangeAsync(
        string sourceFile,
        long offset,
        long length,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.RandomAccess);
        input.Position = offset;
        await CopyExactlyAsync(input, output, length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int got = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (got == 0)
                    throw new EndOfStreamException("Unexpected EOF while building pregap-length recovery candidate.");
                await output.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
                remaining -= got;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int got = await input.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (got == 0)
                throw new EndOfStreamException("Unexpected EOF while reading a Track 01 data sector for pregap recovery.");
            offset += got;
        }
    }

    private static async Task WriteZerosAsync(Stream output, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
            return;
        byte[] zeros = new byte[Math.Min(BufferSize, 1024 * 1024)];
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(zeros.Length, remaining);
            await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static bool VerifyFile(string filePath, HashTarget target, CancellationToken cancellationToken, out string md5Hex)
    {
        if (new FileInfo(filePath).Length != target.Size)
        {
            md5Hex = string.Empty;
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        uint crc = 0;
        try
        {
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);
            int got;
            while ((got = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc = Crc32.Compute(buffer.AsSpan(0, got), crc);
                md5.AppendData(buffer, 0, got);
            }
            md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            return crc == target.Crc32 &&
                   (target.NormalizedMd5 is null || target.NormalizedMd5.Equals(md5Hex, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GetRecoveredOutputPath(string source, HashTarget target, int targetIndex, string outputRoot)
    {
        string fileName = string.IsNullOrWhiteSpace(target.OutputFileName)
            ? $"Track_{targetIndex + 1:00}_{target.NormalizedMd5 ?? target.Crc32Hex}.bin"
            : Path.GetFileName(target.OutputFileName);
        string output = Path.Combine(outputRoot, fileName);
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(source),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            output = Path.Combine(outputRoot,
                Path.GetFileNameWithoutExtension(fileName) + "_recovered" + Path.GetExtension(fileName));
        }
        return output;
    }

    private static string TargetDisplayName(HashTarget target, int index) =>
        string.IsNullOrWhiteSpace(target.Label) ? $"target {index + 1}" : target.Label;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
