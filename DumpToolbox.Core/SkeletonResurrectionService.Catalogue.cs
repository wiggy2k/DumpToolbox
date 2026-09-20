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
        IReadOnlyList<IsoFileExtent> scanFiles = IncludeVerifiedNeroProjects(tree);
        var files = new List<SkeletoolCatalogueImageFile>(scanFiles.Count);
        var neroProjects = new List<NeroNriProjectInfo>(tree.NeroProjects.Count);
        NeroNriDetector.SystemAreaRecord? systemArea = null;
        try
        {
            byte[] sector15 = await reader.ReadForm1SectorAsync(reader.BaseLba + SystemAreaSectors - 1, cancellationToken).ConfigureAwait(false);
            NeroNriDetector.TryParseSystemAreaRecord(sector15, out systemArea);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidOperationException)
        {
            // The filesystem and NRI payload can still be catalogued without sector 15.
        }
        IReadOnlyList<string> neroWarnings = await FindMissingNeroPayloadWarningsAsync(
            reader,
            tree,
            systemArea,
            cancellationToken).ConfigureAwait(false);

        foreach (IsoFileExtent file in scanFiles)
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

            NeroProjectEntry? project = tree.NeroProjects.FirstOrDefault(candidate =>
                candidate.Lba == file.Lba &&
                candidate.DataLength == file.LogicalLength &&
                ("/" + candidate.FileName).Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                bool matchingSystemArea = systemArea is not null &&
                    systemArea.FileName.Equals(project.FileName, StringComparison.OrdinalIgnoreCase) &&
                    systemArea.ExtentLba == project.Lba &&
                    systemArea.DataLength == project.DataLength;
                neroProjects.Add(new NeroNriProjectInfo(
                    project.FileName,
                    project.Lba,
                    project.DataLength,
                    project.Signature,
                    sha1,
                    matchingSystemArea,
                    matchingSystemArea ? systemArea!.PrivateValue : null,
                    project.DirectoryNamespaces));
            }
        }

        return new SkeletoolCatalogueImageContent(
            tree.VolumeIdentifier,
            reader.Kind,
            files,
            "ISO9660",
            neroProjects,
            neroWarnings);
    }

    private static async Task<IReadOnlyList<string>> FindMissingNeroPayloadWarningsAsync(
        SkeletonImageReader image,
        IsoTree tree,
        NeroNriDetector.SystemAreaRecord? systemArea,
        CancellationToken cancellationToken)
    {
        var requirements = tree.NeroProjectRequirements.ToList();
        if (systemArea is not null &&
            !requirements.Any(requirement =>
                requirement.FileName.Equals(systemArea.FileName, StringComparison.OrdinalIgnoreCase) &&
                requirement.ExtentLba == systemArea.ExtentLba &&
                requirement.DataLength == systemArea.DataLength))
        {
            requirements.Add(new NeroNriPayloadRequirement(
                systemArea.FileName,
                systemArea.ExtentLba,
                systemArea.DataLength,
                "valid Nero sector-15 record"));
        }

        var warnings = new List<string>();
        foreach (NeroNriPayloadRequirement requirement in requirements
                     .DistinctBy(item => (item.FileName.ToUpperInvariant(), item.ExtentLba, item.DataLength)))
        {
            bool present = tree.NeroProjects.Any(project =>
                project.FileName.Equals(requirement.FileName, StringComparison.OrdinalIgnoreCase) &&
                project.Lba == requirement.ExtentLba &&
                project.DataLength == requirement.DataLength);
            if (!present)
                present = await HasNeroPayloadSignatureAsync(image, requirement, cancellationToken).ConfigureAwait(false);
            if (!present)
            {
                warnings.Add(
                    $"{requirement.Evidence} requires Nero project '{requirement.FileName}' at LBA {requirement.ExtentLba:N0} " +
                    $"({requirement.DataLength:N0} bytes), but no valid length-prefixed NeroISO payload is present. " +
                    "The NRI payload cannot be generated.");
            }
        }
        return warnings;
    }

    private static IReadOnlyList<IsoFileExtent> IncludeVerifiedNeroProjects(IsoTree tree)
    {
        var files = tree.Files.ToList();
        foreach (NeroProjectEntry project in tree.NeroProjects)
        {
            string path = "/" + project.FileName;
            if (files.Any(file =>
                    file.Lba == project.Lba &&
                    file.LogicalLength == project.DataLength &&
                    file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files.Add(new IsoFileExtent(path, project.Lba, project.DataLength));
        }
        return files;
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
