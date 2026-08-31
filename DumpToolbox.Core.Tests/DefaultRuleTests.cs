using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class DefaultRuleTests
{
    [Fact]
    public void EofDefaultsIncludeBlankSystemIdEasyCdCreatorRule()
    {
        EofSlackRuleSet rules = LoadFreshEofDefaults();

        IReadOnlyList<EofSlackRule> matches = EofSlackRuleService.FindMatches(
            rules,
            systemId: string.Empty,
            applicationId: "EASY CD CREATOR 5.3 (031)",
            dataPreparerId: string.Empty);

        EofSlackRule rule = Assert.Single(matches);
        Assert.Equal(10, rule.DeltaSectors);
        Assert.Equal("MEDIUM", rule.Confidence);

        IReadOnlyList<EofSlackRule> cdRtosMatches = EofSlackRuleService.FindMatches(
            rules,
            systemId: "CD-RTOS CD-BRIDGE",
            applicationId: "EASY CD CREATOR 5.3 (031)",
            dataPreparerId: string.Empty);

        Assert.DoesNotContain(cdRtosMatches, candidate => candidate.DeltaSectors == 10);
    }

    [Fact]
    public void JolietDefaultsSelectEvidenceBackedProfiles()
    {
        JolietNamingRuleSet rules = LoadFreshJolietDefaults();

        AssertProfile(
            rules,
            new IsoMasteringIdentity(
                "APPLE COMPUTER, INC., TYPE: 0002",
                "TOAST ISO 9660 BUILDER",
                string.Empty),
            "Apple/Roxio/Sonic Toast",
            "Level1", "PunctuationElision", "SeparatorInsensitive", "NumericAlias");

        Assert.Null(JolietNamingRuleService.FindMatch(
            rules,
            new IsoMasteringIdentity(
                "UNRELATED SYSTEM",
                "TOAST ISO 9660 BUILDER",
                string.Empty)));

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "HOTBURN V2.0"),
            "Iomega HotBurn 2.0",
            "NumericAlias");

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "CeQuadrat 32bit ISO-9660 Formatter"),
            "CeQuadrat ISO formatter",
            "SeparatorInsensitive", "NumericAlias");

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "CEQUDRAT 32BIT ISO-9660 FORMATTER"),
            "CeQuadrat ISO formatter (legacy spelling)",
            "SeparatorInsensitive", "NumericAlias");

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "ROXIO WINONCD ISO-9660/UDF FORMATTER"),
            "Roxio WinOnCD formatter",
            "SeparatorInsensitive", "NumericAlias");

        Assert.Null(JolietNamingRuleService.FindMatch(
            rules,
            new IsoMasteringIdentity("UNRELATED", "UNRELATED", "UNRELATED")));
    }

    private static void AssertProfile(
        JolietNamingRuleSet rules,
        IsoMasteringIdentity identity,
        string expectedName,
        params string[] expectedMethods)
    {
        JolietNamingProfile profile = Assert.IsType<JolietNamingProfile>(
            JolietNamingRuleService.FindMatch(rules, identity));

        Assert.Equal(expectedName, profile.Name);
        Assert.Equal(
            expectedMethods.Order(StringComparer.OrdinalIgnoreCase),
            profile.Methods.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static EofSlackRuleSet LoadFreshEofDefaults()
    {
        string path = EofSlackRuleService.ExternalFilePath;
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;

        try
        {
            File.Delete(path);
            Assert.True(EofSlackRuleService.EnsureDefaultFileBesideExecutable(out string? error), error);
            return EofSlackRuleService.Load();
        }
        finally
        {
            Restore(path, original);
        }
    }

    private static JolietNamingRuleSet LoadFreshJolietDefaults()
    {
        string path = JolietNamingRuleService.ExternalFilePath;
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;

        try
        {
            File.Delete(path);
            Assert.True(JolietNamingRuleService.EnsureDefaultFileBesideExecutable(out string? error), error);
            return JolietNamingRuleService.Load();
        }
        finally
        {
            Restore(path, original);
        }
    }

    private static void Restore(string path, byte[]? original)
    {
        if (original is null)
            File.Delete(path);
        else
            File.WriteAllBytes(path, original);
    }
}
