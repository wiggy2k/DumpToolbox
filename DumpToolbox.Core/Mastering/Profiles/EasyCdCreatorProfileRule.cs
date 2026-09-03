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
        // Easy CD Creator preserves the primary ISO9660 directory-record sequence in
        // its supplementary tree. Its Joliet path table can still follow UCS-2 order;
        // the exception applies to records within each directory, not directory numbers.
        builder.JolietRecordOrdering = JolietRecordOrdering.PreservePrimaryRecordOrder;
        builder.AddRule($"Easy CD Creator supplementary-root XA file number 0x{rootFileNumber:X2}");
        builder.AddRule("Easy CD Creator supplementary directory records retain primary ISO9660 record order");
        return true;
    }
}
