using System.Security.Cryptography;
using System.Text.Json;

namespace DumpToolbox.Core;

public sealed class IsoExtractionManifest
{
    public int Version { get; set; } = 2;
    public string Format { get; set; } = "DICRecovery ISO Extraction";
    public string SourceImageName { get; set; } = string.Empty;
    public int SourceSectorSize { get; set; }
    public string VolumeIdentifier { get; set; } = string.Empty;
    public string PvdSha256 { get; set; } = string.Empty;
    public bool HasJoliet { get; set; }
    public string VisibleNamespace { get; set; } = "ISO9660";
    public List<IsoExtractionManifestFile> Files { get; set; } = new();
}

public sealed class IsoExtractionManifestFile
{
    // Primary ISO9660 identity. This remains authoritative for DIC reconstruction.
    public string IsoPath { get; set; } = string.Empty;
    // User-visible supplementary identity when an unambiguous Joliet record maps to
    // this primary record's physical payload. Null/empty means no proven mapping.
    public string? JolietPath { get; set; }
    public string ExtractedRelativePath { get; set; } = string.Empty;
    public uint PrimaryDirectoryExtentLba { get; set; }
    public int PrimaryDirectoryRecordOffset { get; set; } = -1;
    public int PrimaryDirectoryRecordIndex { get; set; } = -1;
    public uint? JolietDirectoryExtentLba { get; set; }
    public int? JolietDirectoryRecordOffset { get; set; }
    public int? JolietDirectoryRecordIndex { get; set; }
    public uint ExtentLba { get; set; }
    public long DataLength { get; set; }
    public byte FileFlags { get; set; }
    public int ExtendedAttributeRecordLength { get; set; }
    public int FileUnitSize { get; set; }
    public int InterleaveGapSize { get; set; }
    public List<DicDonorExtent> Extents { get; set; } = new();

    public bool IsAssociated => (FileFlags & 0x04) != 0;
}

public static class IsoExtractionManifestService
{
    public const string ManifestFileName = ".dumptoolbox_iso_manifest.json";
    public const string PrivateDirectoryName = ".dumptoolbox_iso_records";

    public static IsoExtractionManifest? TryLoad(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;

        string path = Path.Combine(rootDirectory, ManifestFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            IsoExtractionManifest? manifest = JsonSerializer.Deserialize<IsoExtractionManifest>(json);
            // v1 manifests remain valid payload catalogues. v2 adds Joliet namespace and
            // directory-record ordering evidence without changing primary identity fields.
            return manifest is { Version: 1 or 2 } ? manifest : null;
        }
        catch
        {
            return null;
        }
    }


    public static bool IsPayloadOnlyCompatible(IsoExtractionManifest manifest, SkeletonInspectionResult inspection, out string reason)
    {
        // Payload-only compatibility deliberately does not trust source-disc identity or
        // geometry. A different pressing may legitimately have a different volume identifier,
        // PVD fingerprint, file LBAs and extent layout while containing byte-identical files.
        // The manifest therefore acts only as a catalogue of extracted payloads; acceptance
        // of an individual record is governed by authoritative destination DIC path/length/flags
        // matching. Never use manifest LBAs/extents in this mode.
        reason = manifest.VolumeIdentifier.Equals(inspection.VolumeIdentifier, StringComparison.OrdinalIgnoreCase)
            ? "source volume identifier happens to match; extractor identity/geometry is not trusted"
            : $"source volume identifier differs (extractor '{manifest.VolumeIdentifier}', DIC '{inspection.VolumeIdentifier}'), which is permitted in payload-only mode; extractor identity/geometry is not trusted";
        return true;
    }

    public static bool MatchesInspection(IsoExtractionManifest manifest, SkeletonInspectionResult inspection, out string reason)
    {
        reason = string.Empty;
        if (!manifest.VolumeIdentifier.Equals(inspection.VolumeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"volume identifier differs (extractor '{manifest.VolumeIdentifier}', DIC '{inspection.VolumeIdentifier}')";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.PvdSha256))
        {
            reason = "extractor manifest does not contain a PVD fingerprint";
            return false;
        }

        try
        {
            byte[] pvd = ReadPrimaryVolumeDescriptor(inspection);
            string hash = Convert.ToHexString(SHA256.HashData(pvd)).ToLowerInvariant();
            if (!hash.Equals(manifest.PvdSha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Primary Volume Descriptor fingerprint differs";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = $"could not verify DIC PVD: {ex.Message}";
            return false;
        }
    }

    private static byte[] ReadPrimaryVolumeDescriptor(SkeletonInspectionResult inspection)
    {
        const int pvdLba = 16;
        using var stream = new FileStream(inspection.SkeletonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] payload = new byte[SkeletonResurrectionService.CookedSectorSize];
        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
        {
            stream.Position = checked((long)(inspection.BaseLba + pvdLba) * SkeletonResurrectionService.CookedSectorSize);
            stream.ReadExactly(payload);
            return payload;
        }

        byte[] raw = new byte[SkeletonResurrectionService.RawSectorSize];
        stream.Position = checked((long)(inspection.BaseLba + pvdLba) * SkeletonResurrectionService.RawSectorSize);
        stream.ReadExactly(raw);
        int userOffset = raw[15] == 2 ? 24 : 16;
        Buffer.BlockCopy(raw, userOffset, payload, 0, payload.Length);
        return payload;
    }

    public static async Task SaveAsync(string rootDirectory, IsoExtractionManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(rootDirectory);
        string path = Path.Combine(rootDirectory, ManifestFileName);
        string temp = path + ".partial";
        try
        {
            // The manifest is written with FileShare.None.  On Windows the handle must be
            // closed before the temporary file can be renamed into place.  Keep the move
            // outside the await-using scope, just as we do for extracted payload files and
            // MDF2BIN output finalisation.
            await using (FileStream stream = new(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}

public sealed record IsoExtractionResult(
    string ImagePath,
    string OutputDirectory,
    string ManifestPath,
    int SectorSize,
    string VolumeIdentifier,
    int FilesExtracted,
    int AssociatedFilesExtracted,
    int DuplicateRecordsPreserved,
    bool HasJoliet,
    int JolietMappedRecords,
    IReadOnlyList<string> Warnings);
