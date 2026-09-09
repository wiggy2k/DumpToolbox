using System.Buffers.Binary;
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
    public void VatReaderUsesIcbFileTypeAndSupportsNonContiguousShortExtents()
    {
        string path = Path.Combine(Path.GetTempPath(), "DumpToolbox_UdfExternalVat_" + Guid.NewGuid().ToString("N") + ".iso");
        const int sectors = 700;
        const int partitionStart = 100;
        const int vatIcbLba = 600;
        try
        {
            byte[] image = new byte[sectors * 2048];
            byte[] vat = new byte[160];
            BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(0, 2), 152);
            BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(132, 4), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(152, 4), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(156, 4), uint.MaxValue);
            vat.AsSpan(0, 80).CopyTo(image.AsSpan((partitionStart + 10) * 2048, 80));
            vat.AsSpan(80, 80).CopyTo(image.AsSpan((partitionStart + 20) * 2048, 80));

            Span<byte> fileEntry = image.AsSpan(vatIcbLba * 2048, 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(fileEntry, 261);
            fileEntry[27] = 248; // ICBTag.FileType
            fileEntry[31] = 0x7F; // Must not be mistaken for FileType.
            BinaryPrimitives.WriteUInt64LittleEndian(fileEntry[56..], (ulong)vat.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(fileEntry[34..], 0); // short_ad
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[168..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[172..], 16);
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[176..], 80);
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[180..], 10);
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[184..], 80);
            BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[188..], 20);
            File.WriteAllBytes(path, image);

            using var payload = UdfImageReader.OpticalPayloadStream.Open(path);
            payload.PhysicalPartitionStart = partitionStart;
            Assert.True(payload.TryReadVatFileAt(vatIcbLba, out _, out byte[] actual));
            Assert.Equal(vat, actual);

            fileEntry = image.AsSpan(vatIcbLba * 2048, 2048);
            fileEntry[27] = 0; // Old VAT type without the required old VAT suffix.
            fileEntry[31] = 0;
            File.WriteAllBytes(path, image);
            using var unsuffixedOldPayload = UdfImageReader.OpticalPayloadStream.Open(path);
            unsuffixedOldPayload.PhysicalPartitionStart = partitionStart;
            Assert.False(unsuffixedOldPayload.TryReadVatFileAt(vatIcbLba, out _, out _));

            fileEntry[27] = 5;
            fileEntry[31] = 0;
            File.WriteAllBytes(path, image);
            using var invalidPayload = UdfImageReader.OpticalPayloadStream.Open(path);
            invalidPayload.PhysicalPartitionStart = partitionStart;
            Assert.False(invalidPayload.TryReadVatFileAt(vatIcbLba, out _, out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UdfStructureInspectorRetainsPartitionMapsAndPreviousVatGenerations()
    {
        string path = Path.Combine(Path.GetTempPath(), "DumpToolbox_UdfEvidence_" + Guid.NewGuid().ToString("N") + ".iso");
        const int partitionStart = 100;
        try
        {
            byte[] image = new byte[700 * 2048];
            Span<byte> anchor = image.AsSpan(256 * 2048, 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(anchor, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor[16..], 32768);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor[20..], 20);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor[24..], 32768);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor[28..], 36);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(20 * 2048, 2), 1);

            Span<byte> partition = image.AsSpan(21 * 2048, 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(partition, 5);
            BinaryPrimitives.WriteUInt16LittleEndian(partition[22..], 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(partition[188..], partitionStart);
            BinaryPrimitives.WriteUInt32LittleEndian(partition[192..], 600);

            Span<byte> lvd = image.AsSpan(22 * 2048, 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(lvd[212..], 2048);
            BinaryPrimitives.WriteUInt32LittleEndian(lvd[264..], 70);
            BinaryPrimitives.WriteUInt32LittleEndian(lvd[268..], 2);
            lvd[440] = 1;
            lvd[441] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(lvd[442..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd[444..], 0x2000);
            lvd[446] = 2;
            lvd[447] = 64;
            System.Text.Encoding.ASCII.GetBytes("*UDF Virtual Partition").CopyTo(lvd[451..]);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd[482..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(lvd[484..], 0x2000);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(23 * 2048, 2), 8);

            WriteImmediateVat(image.AsSpan((partitionStart + 31) * 2048, 2048), uint.MaxValue, 0, 1, reservedByte: 0);
            WriteImmediateVat(image.AsSpan(600 * 2048, 2048), 31, 168, 3, reservedByte: 0x5A);
            File.WriteAllBytes(path, image);

            UdfStructureEvidence evidence = UdfStructureInspector.Inspect(path, default);
            UdfPartitionMapStructureEvidence virtualMap = Assert.Single(evidence.PartitionMaps,
                map => map.Identifier == "*UDF Virtual Partition");
            Assert.Equal((ushort)0x2000, virtualMap.PartitionNumber);
            Assert.Equal(2, evidence.Vats.Count);
            Assert.True(evidence.Vats[0].IsLatest);
            Assert.Equal((uint)31, evidence.Vats[0].PreviousVatIcbLocation);
            Assert.Equal("*Microsoft Windows", evidence.Vats[0].ImplementationIdentifier);
            Assert.Equal(1, evidence.Vats[0].ReservedNonZeroBytes);
            Assert.Equal(uint.MaxValue, evidence.Vats[1].PreviousVatIcbLocation);
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

    private static void WriteImmediateVat(
        Span<byte> fileEntry,
        uint previous,
        uint fileCount,
        uint directoryCount,
        byte reservedByte)
    {
        byte[] vat = new byte[192];
        BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(0, 2), 184);
        BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(2, 2), 32);
        vat[152] = 0;
        System.Text.Encoding.ASCII.GetBytes("*Microsoft Windows").CopyTo(vat, 153);
        BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(132, 4), previous);
        BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(136, 4), fileCount);
        BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(140, 4), directoryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(144, 2), 0x0201);
        BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(146, 2), 0x0201);
        BinaryPrimitives.WriteUInt16LittleEndian(vat.AsSpan(148, 2), 0x0201);
        vat[150] = reservedByte;
        BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(184, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(vat.AsSpan(188, 4), uint.MaxValue);

        BinaryPrimitives.WriteUInt16LittleEndian(fileEntry, 261);
        fileEntry[27] = 248;
        BinaryPrimitives.WriteUInt16LittleEndian(fileEntry[34..], 3);
        BinaryPrimitives.WriteUInt64LittleEndian(fileEntry[56..], (ulong)vat.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[168..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fileEntry[172..], (uint)vat.Length);
        vat.CopyTo(fileEntry[176..]);
    }
}
