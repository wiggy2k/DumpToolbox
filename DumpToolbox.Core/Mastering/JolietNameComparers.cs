using System.Globalization;
using System.Text;

namespace DumpToolbox.Core.Mastering;

/// <summary>
/// Formatter-specific Joliet identifier comparers kept outside the generic directory writer.
/// </summary>
public static class JolietNameComparers
{
    public static IComparer<string> AccentFoldedCaseSensitive { get; } = new AccentFoldedComparer();

    private sealed class AccentFoldedComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            string foldedX = FoldDiacritics(x);
            string foldedY = FoldDiacritics(y);
            int baseCompare = StringComparer.Ordinal.Compare(foldedX, foldedY);
            return baseCompare != 0 ? baseCompare : StringComparer.Ordinal.Compare(x, y);
        }
    }

    private static string FoldDiacritics(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char ch in normalized)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
