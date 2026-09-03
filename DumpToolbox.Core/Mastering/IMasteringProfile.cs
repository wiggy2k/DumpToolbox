namespace DumpToolbox.Core.Mastering;

public enum JolietRecordOrdering
{
    PreservePrimaryRecordOrder,
    CaseSensitiveUcs2Identifier,
    AccentFoldedCaseSensitiveIdentifier
}

public enum JolietPathTableOrdering
{
    PreservePrimaryDirectoryOrder,
    CaseSensitiveUcs2Identifier
}

/// <summary>
/// A formatter/mastering profile exposes policy only.  It must not write sectors
/// or mutate filesystem structures directly.
/// </summary>
public interface IMasteringProfile
{
    string Name { get; }
    IReadOnlyList<string> MatchedRules { get; }
    JolietRecordOrdering JolietRecordOrdering { get; }
    JolietPathTableOrdering JolietPathTableOrdering { get; }
    byte? SupplementaryRootXaFileNumber { get; }
}
