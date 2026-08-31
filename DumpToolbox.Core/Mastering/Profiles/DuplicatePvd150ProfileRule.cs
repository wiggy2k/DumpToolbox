using DumpToolbox.Core.Mastering;
namespace DumpToolbox.Core.Mastering.Profiles;

internal static class DuplicatePvd150ProfileRule
{
    public static bool TryApply(MasteringEvidence evidence, MasteringProfileBuilder builder)
    {
        if (evidence.PrimaryDescriptorVolumeSpaceSizes.Count < 2)
            return false;

        long svdVss = evidence.SupplementaryVolumeSpaceSize;
        bool matched =
            evidence.PrimaryDescriptorVolumeSpaceSizes[0] == checked(svdVss + 150) &&
            evidence.PrimaryDescriptorVolumeSpaceSizes.Skip(1).Any(value => value == svdVss) &&
            (evidence.SupplementaryDescriptorVolumeSpaceSizes.Count == 0 ||
             evidence.SupplementaryDescriptorVolumeSpaceSizes.Any(value => value == svdVss));

        if (!matched)
            return false;

        builder.AddName("Duplicated-PVD -150-sector family");
        builder.JolietRecordOrdering = JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier;
        builder.AddRule("first PVD VSS is SVD VSS + 150 and a later PVD matches the SVD");
        return true;
    }
}
