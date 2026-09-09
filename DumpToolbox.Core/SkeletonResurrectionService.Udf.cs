using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private static async Task<IReadOnlyDictionary<string, SkeletonSourceMatch>> MatchUdfSourceImageAsync(
        SkeletonInspectionResult inspection,
        string imagePath,
        IProgress<SkeletonSourceScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using UdfImageReader udf = UdfImageReader.Open(imagePath);
        var expected = new Dictionary<string, List<(SkeletonContentEntry Entry, bool Xa)>>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonContentEntry entry in inspection.Entries.Where(entry => entry.CanRestore && !entry.IsEmpty))
        {
            if (IsSha1(entry.Sha1)) AddExpected(expected, entry.Sha1!, entry, false);
            if (IsSha1(entry.XaSha1)) AddExpected(expected, entry.XaSha1!, entry, true);
        }

        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = udf.Files.Sum(file => file.Length);
        long processedBytes = 0;
        int processed = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            foreach (UdfImageFile file in udf.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sha1;
                using (Stream source = udf.OpenFile(file.Path))
                using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        hash.AppendData(buffer, 0, read);
                    }
                    sha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }

                if (expected.TryGetValue(sha1, out List<(SkeletonContentEntry Entry, bool Xa)>? targets))
                {
                    foreach ((SkeletonContentEntry entry, bool xa) in targets)
                    {
                        // UDF exposes the logical file stream only. It cannot prove the
                        // 2324-byte raw Form-2 alternate represented by an .XA hash.
                        if (xa)
                            continue;
                        SkeletonContentEntry resolved = ResolveRedumperEntryGeometryForSourceLength(entry, file.Length);
                        if (resolved.DataLength != file.Length)
                            continue;

                        matches[entry.Path] = new SkeletonSourceMatch(
                            resolved,
                            imagePath,
                            sha1,
                            false,
                            "UDF image logical file SHA1",
                            file.Path,
                            SourceLength: file.Length,
                            SourceFilesystem: "UDF");
                        progress?.Report(new SkeletonSourceScanProgress(
                            processed,
                            udf.Files.Count,
                            processedBytes,
                            totalBytes,
                            file.Path,
                            entry.Path,
                            $"{imagePath}::{file.Path}",
                            false));
                    }
                }

                processedBytes += file.Length;
                processed++;
                progress?.Report(new SkeletonSourceScanProgress(
                    processed,
                    udf.Files.Count,
                    processedBytes,
                    totalBytes,
                    file.Path,
                    BytesHashed: processedBytes,
                    FilesHashed: processed));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return matches;
    }
}
