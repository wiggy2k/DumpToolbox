namespace DumpToolbox.Core.Mastering;

internal sealed record MasteringProfile(
    string Name,
    IReadOnlyList<string> MatchedRules,
    JolietRecordOrdering JolietRecordOrdering,
    JolietPathTableOrdering JolietPathTableOrdering,
    byte? SupplementaryRootXaFileNumber) : IMasteringProfile;
