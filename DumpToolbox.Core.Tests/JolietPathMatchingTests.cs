namespace DumpToolbox.Core.Tests;

public sealed class JolietPathMatchingTests
{
    [Theory]
    [InlineData("Videos/Sam& Shara.bik", "VIDEOS/SAM&SH~1.BIK")]
    [InlineData("Videos/Rock'n Roll.bik", "VIDEOS/ROCK'N~1.BIK")]
    [InlineData("Videos/Cash$ Money.bik", "VIDEOS/CASH$M~1.BIK")]
    public void NumericShortAliasesAcceptValidPunctuation(string jolietPath, string primaryPath)
    {
        Assert.True(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath));
        Assert.True(DicLogImportService.SourceJolietPathMatchesPrimaryEntry(jolietPath, primaryPath));
    }

    [Fact]
    public void NumericShortAliasesStillRejectDifferentPrefixes()
    {
        const string jolietPath = "Videos/Other Name.bik";
        const string primaryPath = "VIDEOS/SAM&SH~1.BIK";

        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath));
        Assert.False(DicLogImportService.SourceJolietPathMatchesPrimaryEntry(jolietPath, primaryPath));
    }

    [Fact]
    public void NumericShortAliasesRejectPunctuationOnlyPrefixes()
    {
        const string jolietPath = "Videos/Unrelated.bik";
        const string primaryPath = "VIDEOS/$$$$$$~1.BIK";

        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath));
        Assert.False(DicLogImportService.SourceJolietPathMatchesPrimaryEntry(jolietPath, primaryPath));
    }
}
