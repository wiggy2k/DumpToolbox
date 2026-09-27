using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core.Tests;

public sealed class JolietOrderingCandidateTests
{
    [Theory]
    [InlineData(JolietRecordOrdering.CaseSensitiveUcs2Identifier)]
    [InlineData(JolietRecordOrdering.CaseInsensitiveUcs2Identifier)]
    [InlineData(JolietRecordOrdering.PreservePrimaryRecordOrder)]
    [InlineData(JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier)]
    public void AlternativesTryEveryOtherSupportedOrdering(JolietRecordOrdering defaultOrdering)
    {
        IReadOnlyList<JolietRecordOrdering> alternatives =
            JolietOrderingCandidates.GetAlternatives(defaultOrdering);

        Assert.DoesNotContain(defaultOrdering, alternatives);
        Assert.Equal(Enum.GetValues<JolietRecordOrdering>().Length - 1, alternatives.Count);
        Assert.Equal(alternatives.Count, alternatives.Distinct().Count());
        Assert.Equal(
            Enum.GetValues<JolietRecordOrdering>().Where(value => value != defaultOrdering).ToHashSet(),
            alternatives.ToHashSet());
    }

    [Fact]
    public void CaseInsensitiveAlternativeReproducesAlfabeRecordOrder()
    {
        string[] reconstructed = { "ABC.ICO", "ABC.a6e", "ABC.exe" };
        IComparer<string> comparer = Assert.IsAssignableFrom<IComparer<string>>(
            JolietOrderingCandidates.GetIdentifierComparer(
                JolietRecordOrdering.CaseInsensitiveUcs2Identifier));

        string[] ordered = reconstructed.OrderBy(value => value, comparer).ToArray();

        Assert.Equal(new[] { "ABC.a6e", "ABC.exe", "ABC.ICO" }, ordered);
    }

    [Fact]
    public void EverySupportedOrderingHasAReadableLogName()
    {
        foreach (JolietRecordOrdering ordering in Enum.GetValues<JolietRecordOrdering>())
            Assert.False(string.IsNullOrWhiteSpace(JolietOrderingCandidates.GetDisplayName(ordering)));
    }
}
