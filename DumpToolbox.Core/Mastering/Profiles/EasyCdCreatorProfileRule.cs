using DumpToolbox.Core.Mastering;
namespace DumpToolbox.Core.Mastering.Profiles;

internal static class EasyCdCreatorProfileRule
{
    public static bool TryApply(MasteringEvidence evidence, MasteringProfileBuilder builder)
    {
        if (!evidence.ApplicationIdentifier.Contains("EASY CD CREATOR", StringComparison.OrdinalIgnoreCase))
            return false;

        byte rootFileNumber = evidence.ApplicationIdentifier.Contains(
            "EASY CD CREATOR 4.2 (292)", StringComparison.OrdinalIgnoreCase)
            ? (byte)0x2D
            : (byte)0xCC;

        builder.AddName("Easy CD Creator");
        builder.SupplementaryRootXaFileNumber = rootFileNumber;
        // Updated cross-disc evidence shows that Easy CD Creator preserves both the
        // primary ISO9660 directory-record sequence and primary path-table numbering in
        // its supplementary tree.
        builder.JolietRecordOrdering = JolietRecordOrdering.PreservePrimaryRecordOrder;
        builder.JolietPathTableOrdering = JolietPathTableOrdering.PreservePrimaryDirectoryOrder;
        builder.AddRule($"Easy CD Creator supplementary-root XA file number 0x{rootFileNumber:X2}");
        builder.AddRule("Easy CD Creator supplementary records and path table retain primary ISO9660 order");
        return true;
    }
}
