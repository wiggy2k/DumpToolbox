using DumpToolbox.Core.Mastering;
namespace DumpToolbox.Core.Mastering.Profiles;

internal static class CeQuadratProfileRule
{
    public static bool TryApply(MasteringEvidence evidence, MasteringProfileBuilder builder)
    {
        if (!evidence.HasCeQuadratDirectoryLinkTable)
            return false;

        builder.AddName("CeQuadrat/WinOnCD");
        builder.JolietRecordOrdering = JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier;
        builder.JolietPathTableOrdering = JolietPathTableOrdering.PreservePrimaryDirectoryOrder;
        builder.AddRule("CeQuadrat private Joliet directory-link-table context");
        return true;
    }
}
