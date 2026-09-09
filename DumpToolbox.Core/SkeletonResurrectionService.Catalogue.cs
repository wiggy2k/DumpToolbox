using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    public async Task<SkeletoolCatalogueImageContent> ScanImageContentsForCatalogueAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(imagePath);
        await using SkeletonImageReader reader = await SkeletonImageReader.OpenAsync(fullPath, cancellationToken).ConfigureAwait(false);
        IsoTree tree;
        try
        {
            tree = await ReadIsoTreeAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or EndOfStreamException)
        {
            return await ScanUdfImageContentsForCatalogueAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        var files = new List<SkeletoolCatalogueImageFile>(tree.Files.Count);

        foreach (IsoFileExtent file in tree.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            foreach (SkeletonSourceImageExtent extent in file.LogicalExtents)
            {
                long remaining = extent.Length;
                long lba = extent.Lba;
                while (remaining > 0)
                {
                    byte[] sector = await reader.ReadForm1SectorAsync(lba++, cancellationToken).ConfigureAwait(false);
                    int take = (int)Math.Min(CookedSectorSize, remaining);
                    hash.AppendData(sector, 0, take);
                    remaining -= take;
                }
            }
            string sha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            files.Add(new SkeletoolCatalogueImageFile(file.Path, file.LogicalLength, sha1, file.Lba, file.LogicalExtents));
        }

        return new SkeletoolCatalogueImageContent(tree.VolumeIdentifier, reader.Kind, files, "ISO9660");
    }

    private static async Task<SkeletoolCatalogueImageContent> ScanUdfImageContentsForCatalogueAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        using UdfImageReader udf = UdfImageReader.Open(imagePath);
        var files = new List<SkeletoolCatalogueImageFile>(udf.Files.Count);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            foreach (UdfImageFile file in udf.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Stream source = udf.OpenFile(file.Path);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    hash.AppendData(buffer, 0, read);
                }
                files.Add(new SkeletoolCatalogueImageFile(
                    file.Path,
                    file.Length,
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    null));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new SkeletoolCatalogueImageContent(udf.VolumeIdentifier, null, files, "UDF");
    }
}
