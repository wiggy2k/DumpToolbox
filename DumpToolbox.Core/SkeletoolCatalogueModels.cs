namespace DumpToolbox.Core;

public sealed record SkeletoolCatalogueRoot(
    long Id,
    string Path,
    bool Active,
    DateTimeOffset AddedUtc,
    DateTimeOffset? LastScannedUtc,
    DateTimeOffset? LastSuccessfulScanUtc,
    string? LastError);

public sealed record SkeletoolCatalogueProgress(
    string Phase,
    string CurrentPath,
    int SourcesProcessed,
    int SourcesTotal,
    int ImagesScanned,
    int FilesHashed,
    int SourcesSkipped,
    int SourcesMissing,
    int SourcesErrored = 0);

public sealed record SkeletoolCatalogueImageFile(
    string RelativePath,
    long Size,
    string Sha1,
    long? ImageLba,
    IReadOnlyList<SkeletonSourceImageExtent>? ImageExtents = null);

public sealed record SkeletoolCatalogueImageContent(
    string VolumeIdentifier,
    SkeletonImageKind? ImageKind,
    IReadOnlyList<SkeletoolCatalogueImageFile> Files,
    string ScannerKind);


public sealed record SkeletoolCatalogueMatchSource(
    string UnitKind,
    string SourcePath,
    string UnitSha1,
    long ImageId,
    string ImageEntryPath,
    long SourceOffset,
    long SourceLength,
    string ScannerKind,
    string RelativePath);

public sealed record SkeletoolEvidenceUnit(long Id, string Kind, string SourcePath, string RelativePath, string Sha1);
public sealed record SkeletoolEvidenceImage(long Id, string EntryPath, string DisplayName, long SourceOffset, long SourceLength, string? ImageKind, string ScannerKind, string UnitKind, string SourcePath, string UnitSha1);
