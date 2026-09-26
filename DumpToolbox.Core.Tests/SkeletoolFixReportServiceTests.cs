using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletoolFixReportServiceTests
{
    [Fact]
    public void SeparatesHaveAndMissAndUsesTheMatchedManifestVariant()
    {
        const string emptySha1 = SkeletonResurrectionService.EmptySha1;
        const string primarySha1 = "1111111111111111111111111111111111111111";
        const string xaSha1 = "2222222222222222222222222222222222222222";
        const string missingSha1 = "3333333333333333333333333333333333333333";
        const string ignoredSha1 = "4444444444444444444444444444444444444444";

        var empty = new SkeletonContentEntry(
            "/EMPTY.DAT", 20, 0, emptySha1, null,
            ManifestPath: "Files/Empty File.dat");
        var xa = new SkeletonContentEntry(
            "/MOVIE.STR", 21, 2048, primarySha1, xaSha1,
            ManifestPath: "MOVIE.STR",
            XaManifestPath: "MOVIE.STR.XA");
        var missing = new SkeletonContentEntry(
            "/MISSING.BIN", 22, 2048, missingSha1, null,
            ManifestPath: "Folder/Missing.bin");
        var noHash = new SkeletonContentEntry(
            "/NOHASH.BIN", 23, 2048, null, null);
        var ignored = new SkeletonContentEntry(
            "/UNUSED.BIN", 0, 0, ignoredSha1, null,
            SkeletonSpecialKind.UnmappedHashEntry,
            CanRestore: false,
            ManifestPath: "Unused.bin");

        var inspection = new SkeletonInspectionResult(
            "disc.skeleton",
            "disc.hash",
            SkeletonImageKind.Cooked2048,
            2048,
            0,
            100,
            [empty, xa, missing, noHash, ignored],
            "TEST DISC",
            4,
            1);
        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase)
        {
            [xa.Path] = new SkeletonSourceMatch(xa, "source.bin", xaSha1, IsXa: true)
        };

        string report = SkeletoolFixReportService.Build(
            inspection,
            matches,
            new DateTimeOffset(2026, 9, 26, 12, 30, 0, TimeSpan.FromHours(1)));

        string have = report[report.IndexOf("[HAVE]", StringComparison.Ordinal)..
            report.IndexOf("[MISS]", StringComparison.Ordinal)];
        string miss = report[report.IndexOf("[MISS]", StringComparison.Ordinal)..];

        Assert.Contains("HAVE: 2    MISS: 2    IGNORED MANIFEST-ONLY: 1", report);
        Assert.Contains($"{emptySha1}  Files/Empty File.dat", have);
        Assert.Contains($"{xaSha1}  MOVIE.STR.XA", have);
        Assert.DoesNotContain(primarySha1, report);
        Assert.Contains($"{missingSha1}  Folder/Missing.bin", miss);
        Assert.Contains("(not in .hash)                            NOHASH.BIN", miss);
        Assert.DoesNotContain(ignoredSha1, report);
        Assert.Contains("Generated: 2026-09-26 12:30:00 +01:00", report);
    }
}
