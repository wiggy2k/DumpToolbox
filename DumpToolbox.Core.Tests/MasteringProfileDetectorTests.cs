using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core.Tests;

public sealed class MasteringProfileDetectorTests
{
    [Fact]
    public void EasyCdCreator42SelectsObservedRootFileNumber()
    {
        var evidence = new MasteringEvidence(
            "EASY CD CREATOR 4.2 (292)",
            Array.Empty<long>(),
            Array.Empty<long>(),
            0,
            false,
            "%/E");

        IMasteringProfile profile = MasteringProfileDetector.Detect(evidence);

        Assert.Equal((byte)0x2D, profile.SupplementaryRootXaFileNumber);
        Assert.Equal(JolietRecordOrdering.PreservePrimaryRecordOrder, profile.JolietRecordOrdering);
        Assert.Equal(JolietPathTableOrdering.CaseSensitiveUcs2Identifier, profile.JolietPathTableOrdering);
        Assert.Contains("Easy CD Creator", profile.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePvdEvidenceSelectsAccentFoldedOrdering()
    {
        var evidence = new MasteringEvidence(
            string.Empty,
            new long[] { 10_150, 10_000 },
            new long[] { 10_000 },
            10_000,
            false);

        IMasteringProfile profile = MasteringProfileDetector.Detect(evidence);

        Assert.Equal(JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier, profile.JolietRecordOrdering);
        Assert.Contains("Duplicated-PVD", profile.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEvidenceUsesGenericProfile()
    {
        var evidence = new MasteringEvidence(
            string.Empty,
            Array.Empty<long>(),
            Array.Empty<long>(),
            0,
            false);

        IMasteringProfile profile = MasteringProfileDetector.Detect(evidence);

        Assert.Equal("Generic / evidence-only", profile.Name);
        Assert.Empty(profile.MatchedRules);
        Assert.Equal(JolietRecordOrdering.PreservePrimaryRecordOrder, profile.JolietRecordOrdering);
        Assert.Equal(JolietPathTableOrdering.PreservePrimaryDirectoryOrder, profile.JolietPathTableOrdering);
    }

    [Theory]
    [InlineData("%/@")]
    [InlineData("%/C")]
    [InlineData("%/E")]
    public void JolietEscapeSequenceSelectsStandardsOrdering(string escapeSequence)
    {
        var evidence = new MasteringEvidence(
            string.Empty,
            Array.Empty<long>(),
            Array.Empty<long>(),
            0,
            false,
            escapeSequence);

        IMasteringProfile profile = MasteringProfileDetector.Detect(evidence);

        Assert.Equal(JolietRecordOrdering.CaseSensitiveUcs2Identifier, profile.JolietRecordOrdering);
        Assert.Equal(JolietPathTableOrdering.CaseSensitiveUcs2Identifier, profile.JolietPathTableOrdering);
        Assert.Contains(escapeSequence, profile.MatchedRules.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void CeQuadratRetainsItsProvenPrimaryPathTableOrder()
    {
        var evidence = new MasteringEvidence(
            string.Empty,
            Array.Empty<long>(),
            Array.Empty<long>(),
            0,
            true,
            "%/@");

        IMasteringProfile profile = MasteringProfileDetector.Detect(evidence);

        Assert.Equal(JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier, profile.JolietRecordOrdering);
        Assert.Equal(JolietPathTableOrdering.PreservePrimaryDirectoryOrder, profile.JolietPathTableOrdering);
    }

}
