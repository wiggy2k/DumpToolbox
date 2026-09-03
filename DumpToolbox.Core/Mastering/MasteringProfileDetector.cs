using DumpToolbox.Core.Mastering.Profiles;

namespace DumpToolbox.Core.Mastering;

/// <summary>
/// Central formatter/mastering detector.  Detection is evidence-based and composable:
/// more than one rule may contribute policy to the resulting profile.
/// </summary>
public static class MasteringProfileDetector
{
    public static IMasteringProfile Detect(MasteringEvidence evidence)
    {
        var builder = new MasteringProfileBuilder();
        JolietStandardProfileRule.TryApply(evidence, builder);
        EasyCdCreatorProfileRule.TryApply(evidence, builder);
        DuplicatePvd150ProfileRule.TryApply(evidence, builder);
        CeQuadratProfileRule.TryApply(evidence, builder);
        return builder.Build();
    }
}
