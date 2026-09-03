using System.Buffers.Binary;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class MountedDiscJolietPathServiceTests
{
    [Fact]
    public async Task PairsUniquePrimaryAndJolietRecordsAndEnrichesVerifiedMatch()
    {
        var sectors = new Dictionary<long, byte[]>
        {
            [16] = Descriptor(1, string.Empty, 30, 2048),
            [17] = Descriptor(2, "%/E", 40, 2048),
            [18] = Descriptor(255, string.Empty, 0, 0),
            [30] = Directory(Encoding.ASCII.GetBytes("LONGNA~1.TXT"), 100, 1234),
            [40] = Directory(Encoding.BigEndianUnicode.GetBytes("Long Name.txt"), 100, 1234)
        };

        IReadOnlyDictionary<string, string> map = await MountedDiscJolietPathService.ReadAsync(
            (lba, _) => Task.FromResult(sectors.TryGetValue(lba, out byte[]? sector) ? sector : new byte[2048]),
            (lba, length, _) => Task.FromResult(sectors[(long)lba][..(int)length]),
            default);

        Assert.Equal("Long Name.txt", map["LONGNA~1.TXT"]);
        Assert.True(MountedDiscJolietPathService.TryResolveJolietPath("Long Name.txt", map, out string visiblePath));
        Assert.Equal("Long Name.txt", visiblePath);

        SkeletonContentEntry entry = Entry("/LONGNA~1.TXT", 100, 1234);
        var matches = new Dictionary<string, SkeletonSourceMatch>(StringComparer.OrdinalIgnoreCase)
        {
            [entry.Path] = new(entry, @"D:\LONGNA~1.TXT", string.Empty, false,
                "ISO9660 exact relative path+filename+size", "LONGNA~1.TXT")
        };

        int enriched = MountedDiscJolietPathService.EnrichMatches(matches, map);

        Assert.Equal(1, enriched);
        Assert.Equal("Long Name.txt", matches[entry.Path].SourceRelativePath);
        Assert.Equal(MountedDiscJolietPathService.MatchMethod, matches[entry.Path].MatchMethod);
    }

    [Fact]
    public async Task DoesNotPairAmbiguousSharedGeometry()
    {
        var sectors = new Dictionary<long, byte[]>
        {
            [16] = Descriptor(1, string.Empty, 30, 2048),
            [17] = Descriptor(2, "%/E", 40, 2048),
            [18] = Descriptor(255, string.Empty, 0, 0),
            [30] = DirectoryWithTwoFiles(Encoding.ASCII.GetBytes("FIRST.TXT"), Encoding.ASCII.GetBytes("SECOND.TXT"), 100, 1234),
            [40] = Directory(Encoding.BigEndianUnicode.GetBytes("Visible.txt"), 100, 1234)
        };

        IReadOnlyDictionary<string, string> map = await MountedDiscJolietPathService.ReadAsync(
            (lba, _) => Task.FromResult(sectors.TryGetValue(lba, out byte[]? sector) ? sector : new byte[2048]),
            (lba, length, _) => Task.FromResult(sectors[(long)lba][..(int)length]),
            default);

        Assert.Empty(map);
    }

    private static byte[] Descriptor(byte type, string escapeSequence, uint rootExtent, uint rootLength)
    {
        var bytes = new byte[2048];
        bytes[0] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, 1);
        bytes[6] = 1;
        if (type == 2)
            Encoding.ASCII.GetBytes(escapeSequence).CopyTo(bytes, 88);
        bytes[156] = 34;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(158, 4), rootExtent);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(162, 4), rootExtent);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(166, 4), rootLength);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(170, 4), rootLength);
        bytes[181] = 2;
        bytes[188] = 1;
        bytes[189] = 0;
        return bytes;
    }

    private static byte[] Directory(byte[] identifier, uint extent, uint length)
    {
        var bytes = new byte[2048];
        WriteRecord(bytes, 0, identifier, extent, length);
        return bytes;
    }

    private static byte[] DirectoryWithTwoFiles(byte[] first, byte[] second, uint extent, uint length)
    {
        var bytes = new byte[2048];
        int offset = WriteRecord(bytes, 0, first, extent, length);
        WriteRecord(bytes, offset, second, extent, length);
        return bytes;
    }

    private static int WriteRecord(byte[] destination, int offset, byte[] identifier, uint extent, uint length)
    {
        int recordLength = 33 + identifier.Length + (identifier.Length % 2 == 0 ? 1 : 0);
        destination[offset] = (byte)recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset + 2, 4), extent);
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset + 6, 4), extent);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset + 10, 4), length);
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset + 14, 4), length);
        destination[offset + 25] = 0;
        destination[offset + 28] = 1;
        destination[offset + 31] = 1;
        destination[offset + 32] = (byte)identifier.Length;
        identifier.CopyTo(destination, offset + 33);
        return offset + recordLength;
    }

    private static SkeletonContentEntry Entry(string path, uint extent, long length) =>
        new(path, extent, length, string.Empty, string.Empty,
            SpecialKind: SkeletonSpecialKind.None,
            CanRestore: true,
            RequiresSource: true,
            IsoOriginalPath: path,
            IsoRecordExtentLba: extent);
}

