namespace DumpToolbox.Core.Tests;

public sealed class JolietPathMatchingTests
{
    [Theory]
    [InlineData("Videos/Sam& Shara.bik", "VIDEOS/SAM&SH~1.BIK")]
    [InlineData("Videos/Rock'n Roll.bik", "VIDEOS/ROCK'N~1.BIK")]
    [InlineData("Videos/Cash$ Money.bik", "VIDEOS/CASH$M~1.BIK")]
    [InlineData("Sound/Trivia4CD", "SOUND/TRIVIA~1.")]
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

    [Fact]
    public void OpaqueTildeAliasesRequireExplicitMasteringProfile()
    {
        const string jolietPath = "DirectX/Apr2006_xinput_x86.cab";
        const string primaryPath = "DIRECTX/AP22B5~1.CAB";
        var profile = ProfileWith("OpaqueTildeAlias");

        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath));
        Assert.True(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath, profile));
        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(
            jolietPath, "DIRECTX/AP22B5~1.EXE", profile));
    }

    [Theory]
    [InlineData("Data/Ubisoft Game.exe", "DATA/UBI_0001.EXE")]
    [InlineData("Data/Ubisoft Game.exe", "DATA/UBI_000A.EXE")]
    public void HexOrdinalAliasesRequireExplicitMasteringProfile(string jolietPath, string primaryPath)
    {
        var profile = ProfileWith("HexOrdinalAlias");

        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath));
        Assert.True(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(jolietPath, primaryPath, profile));
        Assert.False(SkeletonResurrectionService.DonorJolietPathProjectsToIsoPath(
            "Data/Different Game.exe", primaryPath, profile));
    }

    private static JolietNamingProfile ProfileWith(params string[] methods) => new(
        "Test",
        "Test",
        "TEST",
        string.Empty,
        "*",
        methods.ToHashSet(StringComparer.OrdinalIgnoreCase));
}
