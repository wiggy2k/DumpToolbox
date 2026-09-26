using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class OpticalImageExtentStreamTests
{
    [Fact]
    public void CookedImageExtentsAreExposedAsOneSeekableStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"extent-stream-{Guid.NewGuid():N}.iso");
        try
        {
            byte[] image = BuildCookedSectors(8);
            File.WriteAllBytes(path, image);
            SkeletonSourceImageExtent[] extents =
            [
                new(1, 2500),
                new(5, 1000)
            ];
            byte[] expected = image.AsSpan(2048, 2500).ToArray()
                .Concat(image.AsSpan(5 * 2048, 1000).ToArray())
                .ToArray();

            using var stream = new OpticalImageExtentStream(path, extents, expected.Length);
            byte[] actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);

            stream.Position = 2400;
            byte[] crossing = new byte[300];
            stream.ReadExactly(crossing);
            Assert.Equal(expected.AsSpan(2400, 300).ToArray(), crossing);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RawImageExtentsRespectTheImageBaseLba()
    {
        string path = Path.Combine(Path.GetTempPath(), $"extent-stream-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] cooked = BuildCookedSectors(6);
            byte[] raw = new byte[6 * SkeletonResurrectionService.RawSectorSize];
            for (int i = 0; i < 6; i++)
            {
                SkeletonResurrectionService.BuildMode1Sector(
                    100 + i,
                    cooked.AsSpan(i * SkeletonResurrectionService.CookedSectorSize, SkeletonResurrectionService.CookedSectorSize),
                    raw.AsSpan(i * SkeletonResurrectionService.RawSectorSize, SkeletonResurrectionService.RawSectorSize));
            }
            File.WriteAllBytes(path, raw);

            SkeletonSourceImageExtent[] extents =
            [
                new(101, 3000),
                new(105, 1000)
            ];
            byte[] expected = cooked.AsSpan(2048, 3000).ToArray()
                .Concat(cooked.AsSpan(5 * 2048, 1000).ToArray())
                .ToArray();

            using var stream = new OpticalImageExtentStream(path, extents, expected.Length);
            byte[] actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static byte[] BuildCookedSectors(int count)
    {
        byte[] data = new byte[count * SkeletonResurrectionService.CookedSectorSize];
        for (int sector = 0; sector < count; sector++)
        {
            for (int offset = 0; offset < SkeletonResurrectionService.CookedSectorSize; offset++)
                data[sector * SkeletonResurrectionService.CookedSectorSize + offset] = (byte)(sector * 31 + offset);
        }
        return data;
    }
}
