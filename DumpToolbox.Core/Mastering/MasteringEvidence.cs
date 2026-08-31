namespace DumpToolbox.Core.Mastering;

/// <summary>
/// Immutable mastering evidence made available to formatter/profile detection.
/// The recovery/synthesis code remains generic; profiles interpret only evidence
/// already recovered from the disc/logs and return policy decisions.
/// </summary>
public sealed record MasteringEvidence(
    string ApplicationIdentifier,
    IReadOnlyList<long> PrimaryDescriptorVolumeSpaceSizes,
    IReadOnlyList<long> SupplementaryDescriptorVolumeSpaceSizes,
    long SupplementaryVolumeSpaceSize,
    bool HasCeQuadratDirectoryLinkTable);
