using DumpToolbox.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core.Tests;

public sealed class SkeletonHashManifestTests
{
    [Fact]
    public async Task InspectionInfersCanonicalSha1ForManifestlessZeroLengthFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"skeletool-empty-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string skeletonPath = Path.Combine(directory, "disc.skeleton");
            string hashPath = Path.Combine(directory, "disc.hash");
            byte[] presentPayload = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(skeletonPath, BuildIsoWithManifestlessFiles(presentPayload));
            string presentSha1 = Convert.ToHexString(SHA1.HashData(presentPayload)).ToLowerInvariant();
            await File.WriteAllTextAsync(hashPath, $"{presentSha1} PRESENT.BIN{Environment.NewLine}");

            SkeletonInspectionResult inspection = await new SkeletonResurrectionService().InspectAsync(
                skeletonPath,
                hashPath);

            SkeletonContentEntry empty = Assert.Single(
                inspection.Entries,
                entry => entry.Path.Equals("/CTLGM.DLL", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, empty.DataLength);
            Assert.Equal(SkeletonResurrectionService.EmptySha1, empty.Sha1);
            Assert.True(empty.Sha1WasInferredFromZeroLength);
            Assert.True(empty.IsEmpty);
            Assert.Equal(["/CTLGM.DLL"], inspection.EmptyFilesWithInferredSha1);
            Assert.Equal(["/MISSING.BIN"], inspection.FilesMissingFromHashManifest);

            string report = SkeletoolFixReportService.Build(
                inspection,
                new Dictionary<string, SkeletonSourceMatch>(),
                new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
            Assert.Contains(
                $"{SkeletonResurrectionService.EmptySha1}  CTLGM.DLL",
                report,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

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
    public async Task MatchesNonAsciiPrimaryIdentifierBytesInHashManifest()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"skeletool-hash-encoding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string hashPath = Path.Combine(directory, "disc.hash");
            const string sha1 = "0123456789abcdef0123456789abcdef01234567";
            string manifestPath = "ARCHIVOS/ANIMAC~1/ESTÁTI~1.ANI";
            byte[] line = Encoding.Latin1.GetBytes($"{sha1} {manifestPath}\r\n");
            Assert.Contains((byte)0xC1, line);
            await File.WriteAllBytesAsync(hashPath, line);

            IReadOnlyList<SkeletonResurrectionService.HashManifestEntry> manifest =
                await SkeletonResurrectionService.ReadHashManifestAsync(hashPath, CancellationToken.None);
            string isoName = SkeletonResurrectionService.DecodePrimaryIsoIdentifier(
                Encoding.Latin1.GetBytes("ESTÁTI~1.ANI"));
            string isoPath = "/ARCHIVOS/ANIMAC~1/" + isoName;

            Assert.Single(manifest);
            Assert.Equal(manifestPath, manifest[0].Path);
            Assert.Empty(SkeletonResurrectionService.FindFilesMissingFromHashManifest(
                [isoPath], manifest.Select(entry => entry.Path)));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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

    private static byte[] BuildIsoWithManifestlessFiles(ReadOnlySpan<byte> presentPayload)
    {
        const int sectorSize = 2048;
        const int sectors = 24;
        const uint rootLba = 20;
        const uint presentLba = 21;
        const uint missingLba = 22;
        byte[] image = new byte[sectors * sectorSize];

        Span<byte> pvd = image.AsSpan(16 * sectorSize, sectorSize);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        pvd.Slice(40, 32).Fill((byte)' ');
        "EMPTY_HASH_TEST"u8.CopyTo(pvd[40..]);
        WriteBothEndian32(pvd, 80, sectors);
        WriteDirectoryRecord(pvd, 156, rootLba, sectorSize, 0x02, [0]);

        Span<byte> terminator = image.AsSpan(17 * sectorSize, sectorSize);
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        Span<byte> directory = image.AsSpan((int)rootLba * sectorSize, sectorSize);
        int offset = 0;
        offset += WriteDirectoryRecord(directory, offset, rootLba, sectorSize, 0x02, [0]);
        offset += WriteDirectoryRecord(directory, offset, rootLba, sectorSize, 0x02, [1]);
        offset += WriteDirectoryRecord(directory, offset, presentLba, 0, 0x00, "CTLGM.DLL;1"u8);
        offset += WriteDirectoryRecord(directory, offset, presentLba, presentPayload.Length, 0x00, "PRESENT.BIN;1"u8);
        WriteDirectoryRecord(directory, offset, missingLba, 4, 0x00, "MISSING.BIN;1"u8);
        presentPayload.CopyTo(image.AsSpan((int)presentLba * sectorSize));
        return image;
    }

    private static int WriteDirectoryRecord(
        Span<byte> target,
        int offset,
        uint extentLba,
        int dataLength,
        byte flags,
        ReadOnlySpan<byte> identifier)
    {
        int length = 33 + identifier.Length + ((identifier.Length & 1) == 0 ? 1 : 0);
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
        return length;
    }

    private static void WriteBothEndian32(Span<byte> target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(offset + 4, 4), value);
    }
}
