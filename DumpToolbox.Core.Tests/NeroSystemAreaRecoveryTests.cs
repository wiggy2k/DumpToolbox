using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class NeroSystemAreaRecoveryTests
{
    [Fact]
    public void ParsesHiddenNriDirectoryRecordFromParentSystemUseArea()
    {
        byte[] parentRecord = Convert.FromHexString(
            "52001300000000000013000800000000080000000000000004020000010000010101" +
            "3000FDB603000003B6FD090500000000050967050F0A101904010000010000010E" +
            "21214D53394638362E4E52493B3100");

        bool found = SkeletonResurrectionService.TryParseEmbeddedNeroProjectDirectoryRecord(
            parentRecord,
            out string fileName,
            out uint extentLba,
            out uint dataLength);

        Assert.True(found);
        Assert.Equal("!!MS9F86.NRI", fileName);
        Assert.Equal(243453u, extentLba);
        Assert.Equal(1289u, dataLength);
    }

    [Fact]
    public async Task IsoExtractorPreservesHiddenNriAndItsSectorMetadata()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroExtract_" + Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempDirectory, "disc.iso");
        string outputPath = Path.Combine(tempDirectory, "extracted");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            byte[] nri = BuildTestNri();
            File.WriteAllBytes(imagePath, BuildNeroTestIso(nri, 0x6719EA98));

            IsoExtractionResult result = await new DicDonorImageService().ExtractAllAsync(imagePath, outputPath);

            NeroNriProjectInfo project = Assert.Single(result.NeroProjects);
            Assert.Equal("!!MS9F86.NRI", project.FileName);
            Assert.Equal("NeroISO0.02.03", project.NeroIsoSignature);
            Assert.Equal(30u, project.ExtentLba);
            Assert.Equal(1289u, project.DataLength);
            Assert.Equal(Convert.ToHexString(SHA1.HashData(nri)).ToLowerInvariant(), project.Sha1);
            Assert.True(project.HasMatchingSystemAreaRecord);
            Assert.Equal(0x6719EA98u, project.SystemAreaPrivateValue);
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("cannot be", StringComparison.OrdinalIgnoreCase));

            IsoExtractionManifest manifest = Assert.IsType<IsoExtractionManifest>(
                IsoExtractionManifestService.TryLoad(outputPath));
            Assert.Equal(4, manifest.Version);
            IsoExtractionManifestFile nriRecord = Assert.Single(manifest.Files);
            Assert.True(nriRecord.IsEmbeddedNeroProject);
            Assert.Equal("NeroISO0.02.03", nriRecord.NeroIsoSignature);
            Assert.Equal(nri, File.ReadAllBytes(Path.Combine(outputPath, nriRecord.ExtractedRelativePath)));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WarnsWhenDirectNeroEvidenceReferencesMissingNriPayload()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroMissing_" + Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempDirectory, "disc.iso");
        string hashPath = Path.Combine(tempDirectory, "disc.hash");
        string outputPath = Path.Combine(tempDirectory, "extracted");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            byte[] missingPayload = new byte[1289];
            byte[] image = BuildNeroTestIso(missingPayload, 0x6719EA98);
            File.WriteAllBytes(imagePath, image);
            string systemAreaSha1 = Convert.ToHexString(SHA1.HashData(image.AsSpan(0, 16 * 2048))).ToLowerInvariant();
            File.WriteAllText(hashPath, $"{systemAreaSha1} SYSTEM_AREA{Environment.NewLine}");

            SkeletonInspectionResult inspection = await new SkeletonResurrectionService()
                .InspectAsync(imagePath, hashPath);
            string skeletonWarning = Assert.Single(inspection.NeroNriWarnings);
            Assert.Contains("!!MS9F86.NRI", skeletonWarning, StringComparison.Ordinal);
            Assert.Contains("cannot be generated", skeletonWarning, StringComparison.OrdinalIgnoreCase);

            IsoExtractionResult extraction = await new DicDonorImageService()
                .ExtractAllAsync(imagePath, outputPath);
            Assert.Empty(extraction.NeroProjects);
            Assert.Contains(extraction.Warnings, warning =>
                warning.Contains("!!MS9F86.NRI", StringComparison.Ordinal) &&
                warning.Contains("cannot be", StringComparison.OrdinalIgnoreCase));

            SkeletoolCatalogueImageContent catalogue = await new SkeletonResurrectionService()
                .ScanImageContentsForCatalogueAsync(imagePath);
            Assert.Contains(catalogue.NeroNriWarnings!, warning =>
                warning.Contains("cannot be generated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotTreatArbitraryNonZeroSystemAreaAsNeroEvidence()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NonNeroSystemArea_" + Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempDirectory, "disc.iso");
        string hashPath = Path.Combine(tempDirectory, "disc.hash");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            byte[] image = BuildNeroTestIso(BuildTestNri(), 0x6719EA98);
            image.AsSpan(15 * 2048, 2048).Fill(0x5a);
            image.AsSpan(20 * 2048, 2048).Clear();
            WriteRootDirectory(image.AsSpan(20 * 2048, 2048), 20, 30, 1289, includeNri: false);
            File.WriteAllBytes(imagePath, image);
            string systemAreaSha1 = Convert.ToHexString(SHA1.HashData(image.AsSpan(0, 16 * 2048))).ToLowerInvariant();
            File.WriteAllText(hashPath, $"{systemAreaSha1} SYSTEM_AREA{Environment.NewLine}");

            SkeletonInspectionResult inspection = await new SkeletonResurrectionService()
                .InspectAsync(imagePath, hashPath);

            Assert.Empty(inspection.NeroNriWarnings);
            Assert.Null(inspection.NeroSystemAreaRecovery);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogueImageScannerIndexesHiddenNri()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroCatalogue_" + Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempDirectory, "disc.iso");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            byte[] nri = BuildTestNri();
            File.WriteAllBytes(imagePath, BuildNeroTestIso(nri, 0x12345678));

            SkeletoolCatalogueImageContent content = await new SkeletonResurrectionService()
                .ScanImageContentsForCatalogueAsync(imagePath);

            SkeletoolCatalogueImageFile file = Assert.Single(content.Files);
            Assert.Equal("/!!MS9F86.NRI", file.RelativePath);
            Assert.Equal(30, file.ImageLba);
            Assert.Equal(nri.Length, file.Size);
            Assert.Equal(Convert.ToHexString(SHA1.HashData(nri)).ToLowerInvariant(), file.Sha1);
            NeroNriProjectInfo project = Assert.Single(content.NeroProjects!);
            Assert.Equal("NeroISO0.02.03", project.NeroIsoSignature);
            Assert.True(project.HasMatchingSystemAreaRecord);
            Assert.Equal(0x12345678u, project.SystemAreaPrivateValue);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractorRecognisesNriEmbeddedOnlyInJolietDirectoryRecord()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroJoliet_" + Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempDirectory, "disc.iso");
        string outputPath = Path.Combine(tempDirectory, "extracted");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            byte[] nri = BuildTestNri();
            File.WriteAllBytes(imagePath, BuildNeroTestIso(nri, 0x11223344, hiddenInJolietOnly: true));

            IsoExtractionResult result = await new DicDonorImageService().ExtractAllAsync(imagePath, outputPath);

            NeroNriProjectInfo project = Assert.Single(result.NeroProjects);
            Assert.Equal("Joliet", project.DirectoryNamespaces);
            Assert.Equal("NeroISO0.02.03", project.NeroIsoSignature);
            Assert.Equal(0x11223344u, project.SystemAreaPrivateValue);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StandaloneNriSignatureIsRecognisedBySourceScanner()
    {
        string path = Path.Combine(Path.GetTempPath(), "!!MS9F86_" + Guid.NewGuid().ToString("N") + ".nri");
        try
        {
            File.WriteAllBytes(path, BuildTestNri());
            Assert.Equal(
                "NeroISO0.02.03",
                await SkeletonResurrectionService.TryReadStandaloneNeroSignatureAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectsHiddenNriFromDicMainInfoDirectorySector()
    {
        byte[] parentRecord = Convert.FromHexString(
            "52001300000000000013000800000000080000000000000004020000010000010101" +
            "3000FDB603000003B6FD090500000000050967050F0A101904010000010000010E" +
            "21214D53394638362E4E52493B3100");
        var directorySector = new byte[2048];
        parentRecord.CopyTo(directorySector, 64);
        var projectSector = new byte[2048];
        byte[] signature = "NeroISO0.02.03"u8.ToArray();
        projectSector[0] = checked((byte)signature.Length);
        signature.CopyTo(projectSector, 1);

        NeroSystemAreaRecoveryInfo? info = DicLogImportService.DetectNeroProjectFromMainInfoSectors(
            new Dictionary<long, byte[]>
            {
                [19] = directorySector,
                [243453] = projectSector
            });

        Assert.NotNull(info);
        Assert.True(info.DetectedFromDic);
        Assert.Equal("!!MS9F86.NRI", info.ProjectFileName);
        Assert.Equal(243453u, info.ProjectExtentLba);
        Assert.Equal(1289u, info.ProjectDataLength);
        Assert.Equal("NeroISO0.02.03", info.NeroIsoSignature);
    }

    [Fact]
    public void DetectsHiddenNriFromTruncatedDicMainInfoDirectoryDump()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroDicLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string logPath = Path.Combine(tempDirectory, "disc_mainInfo.txt");

        try
        {
            File.WriteAllText(logPath,
                "========== LBA[000019, 0x00013]: Main Channel ==========\n" +
                "0000 : 22 00 13 00 00 00 00 00  00 13 00 08 00 00 00 00\n" +
                "0010 : 08 00 00 00 00 00 00 00  20 02 00 00 01 00 00 01\n" +
                "0020 : 01 00 52 00 13 00 00 00  00 00 00 13 00 08 00 00\n" +
                "0030 : 00 00 08 00 00 00 00 00  00 00 20 02 00 00 01 00\n" +
                "0040 : 00 01 01 01 30 00 B7 15  05 00 00 05 15 B7 13 22\n" +
                "0050 : 00 00 00 00 22 13 65 09  18 0D 09 34 20 01 00 00\n" +
                "0060 : 01 00 00 01 0E 21 21 4D  53 30 41 30 33 2E 4E 52\n" +
                "0070 : 49 3B 31 00\n");

            NeroSystemAreaRecoveryInfo? info = DicLogImportService.DetectNeroProjectFromMainInfoLog(
                logPath,
                new Dictionary<long, byte[]>());

            Assert.NotNull(info);
            Assert.Equal("!!MS0A03.NRI", info.ProjectFileName);
            Assert.Equal(333239u, info.ProjectExtentLba);
            Assert.Equal(8723u, info.ProjectDataLength);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildsKnownHulk3NeroSystemArea()
    {
        var info = new NeroSystemAreaRecoveryInfo(
            "!!MS9F86.NRI",
            243453,
            1289,
            "NeroISO0.02.03",
            "f71492b95c5459753560f4c90d0a741bcff41ac9");

        byte[] systemArea = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(info, 0x6719EA98);

        Assert.Equal(16 * 2048, systemArea.Length);
        Assert.All(systemArea.AsSpan(0, 15 * 2048).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(
            "0C21214D53394638362E4E5249FDB60300090500000000006719EA9800000020",
            Convert.ToHexString(systemArea.AsSpan(15 * 2048, 32)));
        Assert.All(systemArea.AsSpan(15 * 2048 + 32).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(info.ExpectedSystemAreaSha1, Convert.ToHexString(SHA1.HashData(systemArea)).ToLowerInvariant());
    }

    [Fact]
    public void FindsPrivateValueWithinConstrainedRange()
    {
        const uint expectedPrivateValue = 0x00000123;
        var seed = new NeroSystemAreaRecoveryInfo(
            "!!MS1234.NRI",
            54321,
            1289,
            "NeroISO0.02.03",
            new string('0', 40));
        string expectedSha1 = Convert.ToHexString(SHA1.HashData(
            SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(seed, expectedPrivateValue))).ToLowerInvariant();
        NeroSystemAreaRecoveryInfo info = seed with { ExpectedSystemAreaSha1 = expectedSha1 };

        uint? actual = SkeletonResurrectionService.FindNeroPrivateValue(
            info,
            0,
            1024,
            maxDegreeOfParallelism: 2);

        Assert.Equal(expectedPrivateValue, actual);
    }

    [Fact]
    public async Task ProducesGeneratedSystemAreaMatch()
    {
        const uint expectedPrivateValue = 0x00000123;
        var seed = new NeroSystemAreaRecoveryInfo(
            "!!MS1234.NRI",
            54321,
            1289,
            "NeroISO0.02.03",
            new string('0', 40));
        string expectedSha1 = Convert.ToHexString(SHA1.HashData(
            SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(seed, expectedPrivateValue))).ToLowerInvariant();
        NeroSystemAreaRecoveryInfo info = seed with { ExpectedSystemAreaSha1 = expectedSha1 };
        var systemArea = new SkeletonContentEntry(
            "SYSTEM_AREA",
            0,
            16 * 2048,
            expectedSha1,
            null,
            SkeletonSpecialKind.SystemArea);
        var inspection = new SkeletonInspectionResult(
            "disc.skeleton",
            "disc.hash",
            SkeletonImageKind.Cooked2048,
            2048,
            0,
            16,
            [systemArea],
            "TEST",
            1,
            0)
        {
            NeroSystemAreaRecovery = info
        };

        SkeletonSourceMatch match = await new SkeletonResurrectionService().RecoverNeroSystemAreaAsync(inspection);

        Assert.Equal("Nero hidden-NRI system-area reconstruction", match.MatchMethod);
        Assert.Equal(expectedSha1, match.Sha1);
        Assert.NotNull(match.GeneratedPayload);
        Assert.Equal(expectedPrivateValue, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            match.GeneratedPayload.AsSpan(15 * 2048 + 24, 4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoversDicPrivateBytesFromWholeImageHashes(bool raw)
    {
        const int sectorCount = 40;
        const uint expectedPrivateValue = 0x6719EA98;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroDic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string imagePath = Path.Combine(tempDirectory, raw ? "disc.bin" : "disc.iso");

        try
        {
            var info = new NeroSystemAreaRecoveryInfo(
                "!!MS9F86.NRI",
                243453,
                1289,
                "NeroISO0.02.03",
                string.Empty)
            {
                DetectedFromDic = true
            };

            byte[] expectedLogicalSystemArea = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(
                info,
                expectedPrivateValue);
            byte[] expectedImage = BuildDicTestImage(raw, sectorCount, expectedLogicalSystemArea);
            byte[] startingImage = BuildDicTestImage(raw, sectorCount, new byte[16 * 2048]);
            File.WriteAllBytes(imagePath, startingImage);

            string expectedCrc = Crc32.Compute(expectedImage).ToString("x8");
            string expectedMd5 = Convert.ToHexString(MD5.HashData(expectedImage)).ToLowerInvariant();
            string expectedSha1 = Convert.ToHexString(SHA1.HashData(expectedImage)).ToLowerInvariant();
            var inspection = new SkeletonInspectionResult(
                imagePath,
                "disc_volDesc.txt",
                raw ? SkeletonImageKind.Raw2352 : SkeletonImageKind.Cooked2048,
                raw ? 2352 : 2048,
                0,
                sectorCount,
                Array.Empty<SkeletonContentEntry>(),
                "TEST",
                0,
                0,
                SkeletonSourceKind.DiscImageCreator,
                expectedCrc,
                expectedMd5,
                expectedSha1)
            {
                NeroSystemAreaRecovery = info
            };

            bool recovered = SkeletonResurrectionService.TryApplyDicNeroSystemArea(
                inspection,
                imagePath);

            Assert.True(recovered);
            Assert.Equal(expectedImage, File.ReadAllBytes(imagePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedumperResurrectionUsesWholeImageCrcAndManifestSystemAreaSha1(bool raw)
    {
        const int sectorCount = 40;
        const uint expectedPrivateValue = 0x6719EA98;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroRedumperCrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string skeletonPath = Path.Combine(tempDirectory, "disc.skeleton");
        string hashPath = Path.Combine(tempDirectory, "disc.hash");
        string logPath = Path.Combine(tempDirectory, "disc.log");
        string outputPath = Path.Combine(tempDirectory, raw ? "resurrected.bin" : "resurrected.iso");

        try
        {
            var seed = new NeroSystemAreaRecoveryInfo(
                "!!MS9F86.NRI",
                243453,
                1289,
                "NeroISO0.02.03",
                string.Empty);
            byte[] expectedLogicalSystemArea = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(
                seed,
                expectedPrivateValue);
            string expectedSystemAreaSha1 = Convert.ToHexString(
                SHA1.HashData(expectedLogicalSystemArea)).ToLowerInvariant();
            NeroSystemAreaRecoveryInfo info = seed with { ExpectedSystemAreaSha1 = expectedSystemAreaSha1 };

            byte[] expectedImage = BuildDicTestImage(raw, sectorCount, expectedLogicalSystemArea);
            byte[] skeletonImage = BuildDicTestImage(raw, sectorCount, new byte[16 * 2048]);
            await File.WriteAllBytesAsync(skeletonPath, skeletonImage);
            await File.WriteAllTextAsync(hashPath, $"{expectedSystemAreaSha1} SYSTEM_AREA{Environment.NewLine}");

            string expectedCrc = Crc32.Compute(expectedImage).ToString("x8");
            string expectedMd5 = Convert.ToHexString(MD5.HashData(expectedImage)).ToLowerInvariant();
            string expectedSha1 = Convert.ToHexString(SHA1.HashData(expectedImage)).ToLowerInvariant();
            await File.WriteAllTextAsync(
                logPath,
                $"dat:{Environment.NewLine}<rom name=\"disc.{(raw ? "bin" : "iso")}\" size=\"{expectedImage.LongLength}\" crc=\"{expectedCrc}\" md5=\"{expectedMd5}\" sha1=\"{expectedSha1}\" />{Environment.NewLine}");

            var systemArea = new SkeletonContentEntry(
                "SYSTEM_AREA",
                0,
                16 * 2048,
                expectedSystemAreaSha1,
                null,
                SkeletonSpecialKind.SystemArea);
            var inspection = new SkeletonInspectionResult(
                skeletonPath,
                hashPath,
                raw ? SkeletonImageKind.Raw2352 : SkeletonImageKind.Cooked2048,
                raw ? 2352 : 2048,
                0,
                sectorCount,
                [systemArea],
                "TEST",
                1,
                0)
            {
                NeroSystemAreaRecovery = info
            };
            var messages = new List<string>();
            var recoveryProgress = new List<NeroSystemAreaRecoveryProgress>();

            SkeletonResurrectionResult result = await new SkeletonResurrectionService().ResurrectAsync(
                inspection,
                new Dictionary<string, SkeletonSourceMatch>(),
                outputPath,
                allowMissing: false,
                activity: new InlineProgress<string>(messages.Add),
                neroSystemAreaProgress: new InlineProgress<NeroSystemAreaRecoveryProgress>(recoveryProgress.Add));

            Assert.Equal(expectedImage, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(1, result.RestoredEntries);
            Assert.Contains(messages, message =>
                message.Contains("CRC32 recovered private bytes 6719EA98", StringComparison.Ordinal));
            Assert.Contains(recoveryProgress, item =>
                item.UsesWholeImageCrc32 && item.PrivateValue == expectedPrivateValue);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsRedumperCrcCandidateWhenManifestSystemAreaSha1DoesNotMatch(bool raw)
    {
        const int sectorCount = 40;
        const uint crcPrivateValue = 0x6719EA98;
        const uint manifestPrivateValue = 0x00000123;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox_NeroRedumperReject_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string imagePath = Path.Combine(tempDirectory, raw ? "disc.bin" : "disc.iso");

        try
        {
            var seed = new NeroSystemAreaRecoveryInfo(
                "!!MS9F86.NRI",
                243453,
                1289,
                "NeroISO0.02.03",
                string.Empty);
            byte[] crcSystemArea = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(seed, crcPrivateValue);
            byte[] manifestSystemArea = SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(seed, manifestPrivateValue);
            string manifestSha1 = Convert.ToHexString(SHA1.HashData(manifestSystemArea)).ToLowerInvariant();
            NeroSystemAreaRecoveryInfo info = seed with { ExpectedSystemAreaSha1 = manifestSha1 };

            byte[] targetImage = BuildDicTestImage(raw, sectorCount, crcSystemArea);
            byte[] startingImage = BuildDicTestImage(raw, sectorCount, new byte[16 * 2048]);
            File.WriteAllBytes(imagePath, startingImage);
            var inspection = new SkeletonInspectionResult(
                imagePath,
                "disc.hash",
                raw ? SkeletonImageKind.Raw2352 : SkeletonImageKind.Cooked2048,
                raw ? 2352 : 2048,
                0,
                sectorCount,
                Array.Empty<SkeletonContentEntry>(),
                "TEST",
                0,
                0)
            {
                NeroSystemAreaRecovery = info
            };

            bool recovered = SkeletonResurrectionService.TryApplyRedumperNeroSystemArea(
                inspection,
                imagePath,
                Crc32.Compute(targetImage));

            Assert.False(recovered);
            Assert.Equal(startingImage, File.ReadAllBytes(imagePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] BuildDicTestImage(
        bool raw,
        int sectorCount,
        ReadOnlySpan<byte> logicalSystemArea)
    {
        if (!raw)
        {
            byte[] cooked = new byte[sectorCount * 2048];
            logicalSystemArea.CopyTo(cooked);
            for (int i = logicalSystemArea.Length; i < cooked.Length; i++)
                cooked[i] = unchecked((byte)(i * 37 + 11));
            return cooked;
        }

        byte[] image = new byte[sectorCount * 2352];
        var payload = new byte[2048];
        for (int lba = 0; lba < sectorCount; lba++)
        {
            if (lba < 16)
            {
                logicalSystemArea.Slice(lba * 2048, 2048).CopyTo(payload);
            }
            else
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = unchecked((byte)(lba * 17 + i * 29));
            }

            SkeletonResurrectionService.BuildMode1Sector(
                lba,
                payload,
                image.AsSpan(lba * 2352, 2352));
        }

        return image;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static byte[] BuildTestNri()
    {
        byte[] nri = new byte[1289];
        byte[] signature = "NeroISO0.02.03"u8.ToArray();
        nri[0] = checked((byte)signature.Length);
        signature.CopyTo(nri, 1);
        byte[] creator = "NERO - BURNING ROM"u8.ToArray();
        nri[25] = checked((byte)creator.Length);
        creator.CopyTo(nri, 26);
        "DISK3"u8.CopyTo(nri.AsSpan(96));
        for (int i = 128; i < nri.Length; i += 37)
            nri[i] = unchecked((byte)(i * 13 + 7));
        return nri;
    }

    private static byte[] BuildNeroTestIso(
        byte[] nri,
        uint privateValue,
        bool hiddenInJolietOnly = false)
    {
        const int sectorSize = 2048;
        const int sectors = 40;
        const uint rootLba = 20;
        const uint nriLba = 30;
        byte[] image = new byte[sectors * sectorSize];

        var info = new NeroSystemAreaRecoveryInfo(
            "!!MS9F86.NRI",
            nriLba,
            checked((uint)nri.Length),
            "NeroISO0.02.03",
            string.Empty);
        SkeletonResurrectionService.BuildNeroSystemAreaForPrivateValue(info, privateValue).CopyTo(image, 0);

        Span<byte> pvd = image.AsSpan(16 * sectorSize, sectorSize);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "DISK3"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, sectors);
        WriteDirectoryRecord(pvd, 156, rootLba, sectorSize, 0x02, [0]);

        int terminatorLba = 17;
        if (hiddenInJolietOnly)
        {
            const uint jolietRootLba = 21;
            Span<byte> svd = image.AsSpan(17 * sectorSize, sectorSize);
            svd[0] = 2;
            "CD001"u8.CopyTo(svd[1..]);
            svd[6] = 1;
            svd[88] = 0x25;
            svd[89] = 0x2f;
            svd[90] = 0x45;
            WriteBothEndian32(svd, 80, sectors);
            WriteDirectoryRecord(svd, 156, jolietRootLba, sectorSize, 0x02, [0]);
            WriteRootDirectory(image.AsSpan((int)jolietRootLba * sectorSize, sectorSize), jolietRootLba, nriLba, nri.Length, includeNri: true);
            terminatorLba = 18;
        }

        Span<byte> terminator = image.AsSpan(terminatorLba * sectorSize, sectorSize);
        terminator[0] = 0xff;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        Span<byte> directory = image.AsSpan((int)rootLba * sectorSize, sectorSize);
        WriteRootDirectory(directory, rootLba, nriLba, nri.Length, includeNri: !hiddenInJolietOnly);
        nri.CopyTo(image, (int)nriLba * sectorSize);
        return image;
    }

    private static void WriteRootDirectory(
        Span<byte> directory,
        uint rootLba,
        uint nriLba,
        int nriLength,
        bool includeNri)
    {
        if (!includeNri)
        {
            WriteDirectoryRecord(directory, 0, rootLba, 2048, 0x02, [0]);
            WriteDirectoryRecord(directory, 34, rootLba, 2048, 0x02, [1]);
            return;
        }

        int outerLength = 82;
        WriteDirectoryRecord(directory, 0, rootLba, 2048, 0x02, [0], outerLength);
        WriteDirectoryRecord(
            directory,
            34,
            nriLba,
            nriLength,
            0x01,
            Encoding.ASCII.GetBytes("!!MS9F86.NRI;1"));
        WriteDirectoryRecord(directory, outerLength, rootLba, 2048, 0x02, [1]);
    }

    private static void WriteDirectoryRecord(
        Span<byte> target,
        int offset,
        uint extentLba,
        int dataLength,
        byte flags,
        ReadOnlySpan<byte> identifier,
        int? forcedLength = null)
    {
        int length = forcedLength ?? (33 + identifier.Length + ((identifier.Length & 1) == 0 ? 1 : 0));
        Span<byte> record = target.Slice(offset, length);
        record.Clear();
        record[0] = checked((byte)length);
        WriteBothEndian32(record, 2, extentLba);
        WriteBothEndian32(record, 10, checked((uint)dataLength));
        record[25] = flags;
        record[28] = 1;
        record[31] = 1;
        record[32] = checked((byte)identifier.Length);
        identifier.CopyTo(record[33..]);
    }

    private static void WriteBothEndian32(Span<byte> target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset + 4, 4), value);
    }
}
