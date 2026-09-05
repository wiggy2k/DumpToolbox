using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core.Mastering.Profiles;

internal static class RoxioBurnEngineProfileRule
{
    public static bool TryApply(MasteringEvidence evidence, MasteringProfileBuilder builder)
    {
        if (!evidence.ApplicationIdentifier.Contains("ROXIO BURN ENGINE 2.1", StringComparison.OrdinalIgnoreCase))
            return false;

        builder.AddName("Roxio Burn Engine 2.1");
        builder.JolietRecordOrdering = JolietRecordOrdering.CaseInsensitiveUcs2Identifier;
        builder.JolietPathTableOrdering = JolietPathTableOrdering.CaseInsensitiveUcs2Identifier;
        builder.AddRule("Roxio Burn Engine 2.1 orders Joliet directory records and path tables case-insensitively");
        return true;
    }
}
