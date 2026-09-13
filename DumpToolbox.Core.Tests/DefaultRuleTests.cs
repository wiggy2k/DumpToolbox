using DumpToolbox.Core;
using DumpToolbox.Core.Mastering;

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
    public void EofDefaultsApplyOnlyFullyProvenResidueDirectly()
    {
        EofSlackRuleSet rules = LoadFreshEofDefaults();

        EofSlackRule finalBuilder = Assert.Single(EofSlackRuleService.FindMatches(
            rules, string.Empty, "FINALBUILDER 8", string.Empty));
        Assert.Equal(32, finalBuilder.DeltaSectors);
        Assert.Equal(EofSlackApplyMode.Direct, finalBuilder.ApplyMode);

        EofSlackRule[] easyCd158 = EofSlackRuleService.FindMatches(
            rules, string.Empty, "EASY CD CREATOR 5.3 (158)", string.Empty).ToArray();
        Assert.Equal(new long[] { 10, 3072 }, easyCd158.Select(rule => rule.DeltaSectors).Order().ToArray());
        Assert.All(easyCd158, rule => Assert.Equal(EofSlackApplyMode.HashTrial, rule.ApplyMode));

        EofSlackRule[] easyCd010 = EofSlackRuleService.FindMatches(
            rules, string.Empty, "EASY CD CREATOR 5.3 (010)", string.Empty).ToArray();
        Assert.Contains(easyCd010, rule => rule.DeltaSectors == 10 && rule.ApplyMode == EofSlackApplyMode.Direct);
        Assert.Contains(easyCd010, rule => rule.DeltaSectors == 3072 && rule.ApplyMode == EofSlackApplyMode.HashTrial);

        Assert.Empty(EofSlackRuleService.FindMatches(
            rules, string.Empty, string.Empty, "QUICKTOPIX 2.20"));
        Assert.Equal(
            EofSlackApplyMode.HashTrial,
            Assert.Single(EofSlackRuleService.FindMatches(
                rules, string.Empty, "ROXIO BURN ENGINE 2.1", string.Empty)).ApplyMode);
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
            "SeparatorInsensitive", "NumericAlias", "OpaqueTildeAlias");

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "CEQUDRAT 32BIT ISO-9660 FORMATTER"),
            "CeQuadrat ISO formatter (legacy spelling)",
            "SeparatorInsensitive", "NumericAlias", "OpaqueTildeAlias");

        AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "ROXIO WINONCD ISO-9660/UDF FORMATTER"),
            "Roxio WinOnCD formatter",
            "SeparatorInsensitive", "NumericAlias", "OpaqueTildeAlias");

        JolietNamingProfile nero = AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, "NERO BURNING ROM", string.Empty),
            "Nero",
            "Level1", "Level2", "PunctuationElision", "SeparatorInsensitive", "NumericAlias", "OpaqueTildeAlias");
        Assert.Equal(JolietFileVersioning.Version1, nero.FileVersioning);
        Assert.Equal(JolietRecordOrdering.CaseSensitiveUcs2Identifier, nero.RecordOrdering);
        Assert.Equal(JolietPathTableOrdering.CaseSensitiveUcs2Identifier, nero.PathTableOrdering);

        JolietNamingProfile finalBuilder = AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, "FINALBUILDER 8", string.Empty),
            "FinalBuilder",
            "Level1", "Level2", "PunctuationElision", "SeparatorInsensitive", "NumericAlias", "OpaqueTildeAlias");
        Assert.Equal(JolietFileVersioning.Version1, finalBuilder.FileVersioning);
        Assert.Equal(JolietRecordOrdering.PreservePrimaryRecordOrder, finalBuilder.RecordOrdering);
        Assert.Equal(JolietPathTableOrdering.PreservePrimaryDirectoryOrder, finalBuilder.PathTableOrdering);

        JolietNamingProfile gear = AssertProfile(
            rules,
            new IsoMasteringIdentity("GEAR CD/DVD PREMASTERING", string.Empty, "GEAR SOFTWARE"),
            "GEAR",
            "Level1", "Level2", "PunctuationElision", "SeparatorInsensitive", "NumericAlias");
        Assert.Equal(JolietFileVersioning.None, gear.FileVersioning);
        Assert.Equal(JolietRecordOrdering.CaseInsensitiveUcs2Identifier, gear.RecordOrdering);
        Assert.Equal(JolietPathTableOrdering.PreservePrimaryDirectoryOrder, gear.PathTableOrdering);

        JolietNamingProfile magicIso = AssertProfile(
            rules,
            new IsoMasteringIdentity(string.Empty, string.Empty, "MagicISO V4.3 from GJPSoft"),
            "MagicISO",
            "Level1", "Level2", "PunctuationElision", "SeparatorInsensitive", "NumericAlias");
        Assert.Equal(JolietFileVersioning.None, magicIso.FileVersioning);

        Assert.Null(JolietNamingRuleService.FindMatch(
            rules,
            new IsoMasteringIdentity(string.Empty, "PRASSI PRIMO CD REP", string.Empty)));

        Assert.Null(JolietNamingRuleService.FindMatch(
            rules,
            new IsoMasteringIdentity("UNRELATED", "UNRELATED", "UNRELATED")));
    }

    private static JolietNamingProfile AssertProfile(
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
        return profile;
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
