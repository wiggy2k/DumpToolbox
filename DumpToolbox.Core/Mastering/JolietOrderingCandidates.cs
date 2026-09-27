namespace DumpToolbox.Core.Mastering;

/// <summary>
/// Enumerates the deterministic Joliet directory-record orders that can be tested
/// safely against an original whole-image hash after a complete DIC rebuild.
/// </summary>
public static class JolietOrderingCandidates
{
    private static readonly JolietRecordOrdering[] PreferredOrder =
    {
        JolietRecordOrdering.CaseSensitiveUcs2Identifier,
        JolietRecordOrdering.CaseInsensitiveUcs2Identifier,
        JolietRecordOrdering.PreservePrimaryRecordOrder,
        JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier
    };

    public static IReadOnlyList<JolietRecordOrdering> GetAlternatives(
        JolietRecordOrdering defaultOrdering)
        => PreferredOrder.Where(ordering => ordering != defaultOrdering).ToArray();

    public static string GetDisplayName(JolietRecordOrdering ordering)
        => ordering switch
        {
            JolietRecordOrdering.CaseSensitiveUcs2Identifier => "case-sensitive Joliet ordering",
            JolietRecordOrdering.CaseInsensitiveUcs2Identifier => "case-insensitive Joliet ordering",
            JolietRecordOrdering.PreservePrimaryRecordOrder => "primary ISO9660 record order",
            JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier => "accent-folded case-sensitive Joliet ordering",
            _ => ordering.ToString()
        };

    internal static IComparer<string>? GetIdentifierComparer(JolietRecordOrdering ordering)
        => ordering switch
        {
            JolietRecordOrdering.CaseSensitiveUcs2Identifier => StringComparer.Ordinal,
            JolietRecordOrdering.CaseInsensitiveUcs2Identifier => StringComparer.OrdinalIgnoreCase,
            JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier => JolietNameComparers.AccentFoldedCaseSensitive,
            _ => null
        };
}
