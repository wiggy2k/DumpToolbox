using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core.Mastering.Profiles;

internal static class JolietStandardProfileRule
{
    public static bool TryApply(MasteringEvidence evidence, MasteringProfileBuilder builder)
    {
        if (evidence.JolietEscapeSequence is not ("%/@" or "%/C" or "%/E"))
            return false;

        builder.AddName("Joliet UCS-2");
        builder.JolietRecordOrdering = JolietRecordOrdering.CaseSensitiveUcs2Identifier;
        builder.JolietPathTableOrdering = JolietPathTableOrdering.CaseSensitiveUcs2Identifier;
        builder.AddRule($"SVD escape sequence '{evidence.JolietEscapeSequence}' declares big-endian UCS-2 ordering");
        return true;
    }
}
