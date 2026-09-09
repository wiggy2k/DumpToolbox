using System.Text.Json;

namespace DumpToolbox.Core.Tests;

public sealed class UdfImageReaderTests
{
    [Theory]
    [InlineData("disc.iso", true)]
    [InlineData("disc.bin", true)]
    [InlineData("disc.img", true)]
    [InlineData("disc.cue", false)]
    public void Sha1CatalogueRecognizesSupportedDiscImageExtensions(string path, bool expected)
    {
        Assert.Equal(expected, SkeletoolCatalogueService.IsDirectImage(path));
    }

    [Fact]
    public void RawOpticalPayloadStreamExposesMode1AndMode2Form1UserData()
    {
        string path = Path.Combine(Path.GetTempPath(), "DumpToolbox_UdfPayload_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            byte[] mode1 = BuildRawSector(mode: 1, form2: false, fill: 0x11);
            byte[] mode2 = BuildRawSector(mode: 2, form2: false, fill: 0x22);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                output.Write(mode1);
                output.Write(mode2);
            }

            using var payload = UdfImageReader.OpticalPayloadStream.Open(path);
            Assert.Equal(2352, payload.SourceSectorSize);
            Assert.Equal(4096, payload.Length);

            byte[] logical = new byte[4096];
            payload.ReadExactly(logical);
            Assert.All(logical[..2048], value => Assert.Equal((byte)0x11, value));
            Assert.All(logical[2048..], value => Assert.Equal((byte)0x22, value));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RawOpticalPayloadStreamRejectsMode2Form2AsUdfMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), "DumpToolbox_UdfForm2_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, BuildRawSector(mode: 2, form2: true, fill: 0x33));
            using var payload = UdfImageReader.OpticalPayloadStream.Open(path);
            Assert.Throws<InvalidDataException>(() => payload.ReadByte());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VersionThreeUdfExtractorManifestLoadsOnlyAsPayloadEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DumpToolbox_UdfManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new IsoExtractionManifest
            {
                SourceFilesystem = "UDF",
                HasUdf = true,
                VolumeIdentifier = "UDF DISC",
                Files =
                {
                    new IsoExtractionManifestFile
                    {
                        IsoPath = "/FILE.BIN",
                        UdfPath = "/File.bin",
                        DataLength = 123
                    }
                }
            };
            File.WriteAllText(
                Path.Combine(directory, IsoExtractionManifestService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            IsoExtractionManifest loaded = Assert.IsType<IsoExtractionManifest>(IsoExtractionManifestService.TryLoad(directory));
            var inspection = new SkeletonInspectionResult(
                "unused.bin", "unused.hash", SkeletonImageKind.Raw2352, 2352, 0, 1,
                Array.Empty<SkeletonContentEntry>(), "UDF DISC", 0, 0);
            Assert.False(IsoExtractionManifestService.MatchesInspection(loaded, inspection, out string reason));
            Assert.Contains("UDF-only", reason, StringComparison.OrdinalIgnoreCase);
            Assert.True(IsoExtractionManifestService.IsPayloadOnlyCompatible(loaded, inspection, out _));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static byte[] BuildRawSector(byte mode, bool form2, byte fill)
    {
        byte[] raw = new byte[2352];
        byte[] sync = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        sync.CopyTo(raw, 0);
        raw[15] = mode;
        int offset = mode == 1 ? 16 : 24;
        if (mode == 2)
        {
            byte submode = form2 ? (byte)0x20 : (byte)0x08;
            raw[18] = raw[22] = submode;
        }
        Array.Fill(raw, fill, offset, 2048);
        return raw;
    }
}
