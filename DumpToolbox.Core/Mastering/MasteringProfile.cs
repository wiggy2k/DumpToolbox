namespace DumpToolbox.Core.Mastering;

internal sealed record MasteringProfile(
    string Name,
    IReadOnlyList<string> MatchedRules,
    JolietRecordOrdering JolietRecordOrdering,
    byte? SupplementaryRootXaFileNumber) : IMasteringProfile;
