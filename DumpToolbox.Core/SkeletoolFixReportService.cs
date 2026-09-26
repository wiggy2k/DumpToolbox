using System.Text;

namespace DumpToolbox.Core;

public static class SkeletoolFixReportService
{
    public static string Build(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(matches);

        SkeletonContentEntry[] relevantEntries = inspection.Entries
            .Where(entry => entry.SpecialKind != SkeletonSpecialKind.UnmappedHashEntry)
            .ToArray();
        SkeletonContentEntry[] have = relevantEntries
            .Where(entry => IsAvailable(inspection, matches, entry))
            .OrderBy(GetSortPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SkeletonContentEntry[] miss = relevantEntries
            .Where(entry => !IsAvailable(inspection, matches, entry))
            .OrderBy(GetSortPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int ignored = inspection.Entries.Count(entry =>
            entry.SpecialKind == SkeletonSpecialKind.UnmappedHashEntry);

        string newline = Environment.NewLine;
        var report = new StringBuilder();
        report.Append("SkeleTool fix report").Append(newline);
        report.Append("Generated: ")
            .Append((generatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .Append(newline);
        report.Append("Skeleton: ").Append(inspection.SkeletonPath).Append(newline);
        report.Append("Hash manifest: ").Append(inspection.HashPath).Append(newline);
        report.Append("Volume: ").Append(inspection.VolumeIdentifier).Append(newline);
        report.Append("HAVE: ").Append(have.Length)
            .Append("    MISS: ").Append(miss.Length);
        if (ignored > 0)
            report.Append("    IGNORED MANIFEST-ONLY: ").Append(ignored);
        report.Append(newline).Append(newline);

        AppendSection(report, "HAVE", have, matches, newline);
        report.Append(newline);
        AppendSection(report, "MISS", miss, matches, newline);

        return report.ToString();
    }

    private static bool IsAvailable(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        SkeletonContentEntry entry)
    {
        if (matches.ContainsKey(entry.Path))
            return true;
        if (!entry.CanRestore)
            return false;
        if (entry.IsEmpty)
            return true;
        return entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
               (string.Equals(
                    entry.Sha1,
                    SkeletonResurrectionService.ZeroSystemAreaSha1,
                    StringComparison.OrdinalIgnoreCase) ||
                SkeletonResurrectionService.CanGenerateSystemArea(inspection));
    }

    private static void AppendSection(
        StringBuilder report,
        string heading,
        IReadOnlyList<SkeletonContentEntry> entries,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string newline)
    {
        report.Append('[').Append(heading).Append(']').Append(newline);
        report.Append("SHA-1                                    PATH FROM .HASH").Append(newline);
        report.Append("----------------------------------------  ----------------").Append(newline);

        if (entries.Count == 0)
        {
            report.Append("(none)").Append(newline);
            return;
        }

        foreach (SkeletonContentEntry entry in entries)
        {
            matches.TryGetValue(entry.Path, out SkeletonSourceMatch? match);
            (string sha1, string path) = GetManifestIdentity(entry, match);
            report.Append(sha1.PadRight(40)).Append("  ").Append(path).Append(newline);
        }
    }

    private static (string Sha1, string Path) GetManifestIdentity(
        SkeletonContentEntry entry,
        SkeletonSourceMatch? match)
    {
        if (match?.IsXa == true && !string.IsNullOrWhiteSpace(entry.XaSha1))
        {
            return (
                entry.XaSha1,
                entry.XaManifestPath ?? BuildFallbackManifestPath(entry.Path, xa: true));
        }

        if (!string.IsNullOrWhiteSpace(entry.Sha1))
        {
            return (
                entry.Sha1,
                entry.ManifestPath ?? BuildFallbackManifestPath(entry.Path, xa: false));
        }

        if (!string.IsNullOrWhiteSpace(entry.XaSha1))
        {
            return (
                entry.XaSha1,
                entry.XaManifestPath ?? BuildFallbackManifestPath(entry.Path, xa: true));
        }

        return ("(not in .hash)", BuildFallbackManifestPath(entry.Path, xa: false));
    }

    private static string GetSortPath(SkeletonContentEntry entry) =>
        entry.ManifestPath ?? entry.XaManifestPath ?? entry.Path;

    private static string BuildFallbackManifestPath(string path, bool xa)
    {
        string value = path.StartsWith('/') ? path[1..] : path;
        return xa ? value + ".XA" : value;
    }
}
