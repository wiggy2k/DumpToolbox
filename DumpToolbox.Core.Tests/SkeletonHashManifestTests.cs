using DumpToolbox.Core;
using System.Security.Cryptography;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletonHashManifestTests
{
    [Fact]
    public void FindsSkeletonFilesWithoutAnExactManifestEntry()
    {
        string[] skeletonFiles =
        [
            "/README.TXT",
            "/DATA/ONE.BIN",
            "/REAL.XA",
            "/EMPTY.DAT",
            "/data/one.bin"
        ];
        string[] manifestPaths =
        [
            "readme.txt",
            "DATA/ONE.BIN.XA",
            "REAL.XA",
            "SYSTEM_AREA",
            "GAP_1234"
        ];

        IReadOnlyList<string> missing = SkeletonResurrectionService.FindFilesMissingFromHashManifest(
            skeletonFiles,
            manifestPaths);

        Assert.Equal(["/DATA/ONE.BIN", "/EMPTY.DAT"], missing);
    }

    [Fact]
    public async Task SourceMatchingIgnoresUnmappedManifestEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"skeletool-ignored-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] content = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(Path.Combine(directory, "unwanted.bin"), content);
            string sha1 = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
            var ignored = new SkeletonContentEntry(
                "/UNWANTED.BIN",
                0,
                content.Length,
                sha1,
                null,
                SkeletonSpecialKind.UnmappedHashEntry,
                CanRestore: false);
            var inspection = new SkeletonInspectionResult(
                "disc.skeleton",
                "disc.hash",
                SkeletonImageKind.Cooked2048,
                2048,
                0,
                1,
                [ignored],
                "TEST",
                1,
                1);

            IReadOnlyDictionary<string, SkeletonSourceMatch> matches =
                await new SkeletonResurrectionService().MatchSourcesAsync(
                    inspection,
                    directory,
                    recursive: false,
                    forceRehash: true);

            Assert.Empty(matches);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
