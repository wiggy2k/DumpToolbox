using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class DicCeQuadratSourceMatchingTests
{
    [Fact]
    public async Task ByteIdenticalNumericAliasTieRetainsLiteralSourcePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-cequadrat-match-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string data = Path.Combine(source, "GAME", "DATA");
        Directory.CreateDirectory(data);
        try
        {
            byte[] payload = Encoding.ASCII.GetBytes("identical payload");
            string firstPath = Path.Combine(data, "TESTFR_1.AI");
            string fourthPath = Path.Combine(data, "TESTFR_4.AI");
            await File.WriteAllBytesAsync(firstPath, payload);
            await File.WriteAllBytesAsync(fourthPath, payload);

            string skeletonPath = Path.Combine(root, "disc.skeleton");
            byte[] skeleton = new byte[17 * 2048];
            int pvdOffset = 16 * 2048;
            skeleton[pvdOffset] = 1;
            "CD001"u8.CopyTo(skeleton.AsSpan(pvdOffset + 1));
            Encoding.ASCII.GetBytes("CEQUADRAT 32BIT ISO-9660 FORMATTER")
                .CopyTo(skeleton, pvdOffset + 446);
            await File.WriteAllBytesAsync(skeletonPath, skeleton);

            SkeletonContentEntry[] entries =
            [
                new("/GAME/DATA/TESTFR_1.AI", 121112, payload.Length, null, null,
                    RequiresSource: true, IsoOriginalPath: "/GAME/DATA/TESTFR_1.AI"),
                new("/GAME/DATA/TESTFR_4.AI", 121118, payload.Length, null, null,
                    RequiresSource: true, IsoOriginalPath: "/GAME/DATA/TESTFR_4.AI")
            ];
            var inspection = new SkeletonInspectionResult(
                skeletonPath,
                string.Empty,
                SkeletonImageKind.Cooked2048,
                2048,
                0,
                17,
                entries,
                "TEST",
                0,
                0,
                SkeletonSourceKind.DiscImageCreator);

            IReadOnlyDictionary<string, SkeletonSourceMatch> matches =
                await new SkeletonResurrectionService().MatchSourcesAsync(
                    inspection,
                    source,
                    recursive: true,
                    forceRehash: false,
                    useHistoryDatabase: false);

            Assert.Equal(firstPath, matches[entries[0].Path].SourcePath);
            Assert.Equal(fourthPath, matches[entries[1].Path].SourcePath);
            Assert.All(matches.Values, match =>
                Assert.Equal("ISO9660 exact relative path+filename+size", match.MatchMethod));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PayloadComparisonRequiresEveryByteToMatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dumptoolbox-cequadrat-bytes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string firstPath = Path.Combine(root, "first.bin");
            string samePath = Path.Combine(root, "same.bin");
            string differentPath = Path.Combine(root, "different.bin");
            await File.WriteAllBytesAsync(firstPath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(samePath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(differentPath, [1, 2, 3, 5]);

            Assert.True(SkeletonResurrectionService.DicSourceFilesHaveIdenticalPayloads(firstPath, samePath));
            Assert.False(SkeletonResurrectionService.DicSourceFilesHaveIdenticalPayloads(firstPath, differentPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
