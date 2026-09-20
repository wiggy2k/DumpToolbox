using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.ZStandard;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletonInputMaterializerTests
{
    [Fact]
    public async Task MaterializesLegacyLzmaAloneSkeleton()
    {
        string directory = CreateTestDirectory();
        try
        {
            byte[] expected = CreatePayload();
            string source = Path.Combine(directory, "legacy.skeleton");

            byte[] properties;
            byte[] compressed;
            using (var body = new MemoryStream())
            {
                await using (LzmaStream encoder = LzmaStream.Create(
                                 new LzmaEncoderProperties(), isLzma2: false, body))
                {
                    properties = encoder.Properties;
                    await encoder.WriteAsync(expected);
                }
                compressed = body.ToArray();
            }

            byte[] lzmaAlone = new byte[13 + compressed.Length];
            properties.CopyTo(lzmaAlone, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(lzmaAlone.AsSpan(5, 8), (ulong)expected.Length);
            compressed.CopyTo(lzmaAlone, 13);
            await File.WriteAllBytesAsync(source, lzmaAlone);

            PreparedSkeletonInput prepared = await SkeletonInputMaterializer.PrepareAsync(
                source, progress: null, CancellationToken.None);

            Assert.True(prepared.WasMaterialized);
            Assert.Equal("legacy raw LZMA skeleton", prepared.Format);
            Assert.Equal(source + ".temp", prepared.Path);
            Assert.Equal(expected, await File.ReadAllBytesAsync(prepared.Path));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task MaterializesZstandardSkeleton()
    {
        string directory = CreateTestDirectory();
        try
        {
            byte[] expected = CreatePayload();
            string source = Path.Combine(directory, "modern.skeleton.zst");
            await using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            await using (var compressor = new CompressionStream(output, level: 3, bufferSize: 1024 * 1024, leaveOpen: true))
            {
                await compressor.WriteAsync(expected);
            }

            PreparedSkeletonInput prepared = await SkeletonInputMaterializer.PrepareAsync(
                source, progress: null, CancellationToken.None);

            Assert.True(prepared.WasMaterialized);
            Assert.Equal("Zstandard-compressed skeleton", prepared.Format);
            Assert.Equal(Path.Combine(directory, "modern.skeleton"), prepared.Path);
            Assert.Equal(expected, await File.ReadAllBytesAsync(prepared.Path));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractsTheSkeletonEntryFromZipArchive()
    {
        string directory = CreateTestDirectory();
        try
        {
            byte[] expected = CreatePayload();
            string source = Path.Combine(directory, "disc.zip");
            using (ZipArchive archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                ZipArchiveEntry readme = archive.CreateEntry("readme.txt");
                await using (Stream stream = readme.Open())
                    await stream.WriteAsync("not a skeleton"u8.ToArray());

                ZipArchiveEntry skeleton = archive.CreateEntry("nested/disc.skeleton");
                await using Stream skeletonStream = skeleton.Open();
                await skeletonStream.WriteAsync(expected);
            }

            PreparedSkeletonInput prepared = await SkeletonInputMaterializer.PrepareAsync(
                source, progress: null, CancellationToken.None);

            Assert.True(prepared.WasMaterialized);
            Assert.Contains("archive-contained skeleton", prepared.Format);
            Assert.Equal(Path.Combine(directory, "disc.skeleton"), prepared.Path);
            Assert.Equal(expected, await File.ReadAllBytesAsync(prepared.Path));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void RecognizesTheHistoricalRallyXtremeHeader()
    {
        byte[] header = Convert.FromHexString("5D00000001F0D54F2600000000");

        bool detected = SkeletonInputMaterializer.TryReadLzmaAloneHeader(
            header, compressedLength: 506_709, out long? outputLength);

        Assert.True(detected);
        Assert.Equal(642_766_320, outputLength);
    }

    private static byte[] CreatePayload()
    {
        byte[] pattern = "DumpToolbox compressed skeleton regression data\r\n"u8.ToArray();
        byte[] payload = new byte[pattern.Length * 4096];
        for (int offset = 0; offset < payload.Length; offset += pattern.Length)
            pattern.CopyTo(payload, offset);
        return payload;
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"skeleton-input-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
