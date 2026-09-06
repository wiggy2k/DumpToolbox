using DumpToolbox.Core;
using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core.Tests;

public sealed class DicJolietPaddingTests
{
    [Fact]
    public void EvenLengthJolietIdentifierExposesTrailingPaddingByte()
    {
        const int recordOffset = 7;
        const int identifierLength = 52;
        const int recordLength = 86;
        byte[] directory = new byte[recordOffset + recordLength];
        directory[recordOffset + 33 + identifierLength] = 0xA3;

        byte? value = DicDonorImageService.TryReadIdentifierPadding(
            directory,
            recordOffset,
            recordLength,
            identifierLength);

        Assert.Equal((byte)0xA3, value);
    }

    [Fact]
    public void OddLengthJolietIdentifierHasNoTrailingPaddingByte()
    {
        byte[] directory = new byte[100];

        byte? value = DicDonorImageService.TryReadIdentifierPadding(
            directory,
            recordOffset: 0,
            recordLength: 86,
            identifierLength: 51);

        Assert.Null(value);
    }

    [Fact]
    public void RebuildingMode1ProtectionAfterPaddingPatchMatchesFreshSector()
    {
        const long lba = 12_345;
        byte[] payload = Enumerable.Range(0, 2048)
            .Select(index => (byte)(index * 19 + 7))
            .ToArray();
        byte[] patched = new byte[2352];
        SkeletonResurrectionService.BuildMode1Sector(lba, payload, patched);

        payload[321] = 0xA3;
        patched[16 + 321] = 0xA3;
        SkeletonResurrectionService.RebuildForm1ProtectionFields(patched);

        byte[] expected = new byte[2352];
        SkeletonResurrectionService.BuildMode1Sector(lba, payload, expected);
        Assert.Equal(expected, patched);
    }

    [Fact]
    public async Task CandidateCopiesImageAndChangesOnlyMatchedJolietPadding()
    {
        string root = Path.Combine(Path.GetTempPath(), "DumpToolboxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "rebuilt.iso");
        string candidatePath = Path.Combine(root, "candidate.iso");
        try
        {
            const int directoryLba = 21;
            const int fileRecordOffset = 68;
            const int filePaddingOffset = fileRecordOffset + 33 + 4;
            byte[] image = new byte[40 * 2048];
            WriteDescriptor(image.AsSpan(16 * 2048, 2048), type: 1, rootLba: 20, joliet: false);
            WriteDescriptor(image.AsSpan(17 * 2048, 2048), type: 2, rootLba: directoryLba, joliet: true);
            WriteDescriptor(image.AsSpan(18 * 2048, 2048), type: 0xFF, rootLba: 0, joliet: false);
            WriteDirectoryRecord(image.AsSpan(directoryLba * 2048, 2048), 0, [0], directoryLba, 2048, 0x02);
            WriteDirectoryRecord(image.AsSpan(directoryLba * 2048, 2048), 34, [1], directoryLba, 2048, 0x02);
            WriteDirectoryRecord(image.AsSpan(directoryLba * 2048, 2048), fileRecordOffset, Encoding.BigEndianUnicode.GetBytes("AB"), 30, 3, 0);
            await File.WriteAllBytesAsync(sourcePath, image);

            var inspection = new SkeletonInspectionResult(
                sourcePath, "disc.hash", SkeletonImageKind.Cooked2048, 2048, 0, 40,
                Array.Empty<SkeletonContentEntry>(), "TEST", 0, 0);
            var evidence = new DicJolietPaddingEvidence(
                "AB", 30, 3, 0, directoryLba, fileRecordOffset, 38, 4, 0xA3);

            DicJolietPaddingCandidateResult result = await new DicDonorImageService()
                .CreateJolietPaddingCandidateAsync(
                    inspection, sourcePath, candidatePath, [evidence]);

            Assert.Equal(1, result.AppliedRecords);
            byte[] candidate = await File.ReadAllBytesAsync(candidatePath);
            Assert.Equal(0xA3, candidate[directoryLba * 2048 + filePaddingOffset]);
            candidate[directoryLba * 2048 + filePaddingOffset] = 0;
            Assert.Equal(image, candidate);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void WriteDescriptor(Span<byte> sector, byte type, uint rootLba, bool joliet)
    {
        sector[0] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(sector[1..]);
        sector[6] = 1;
        if (joliet)
            Encoding.ASCII.GetBytes("%/E").CopyTo(sector[88..]);
        if (type is not (1 or 2))
            return;

        Encoding.ASCII.GetBytes("TEST").CopyTo(sector[40..]);
        sector[156] = 34;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[158..162], rootLba);
        BinaryPrimitives.WriteUInt32BigEndian(sector[162..166], rootLba);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[166..170], 2048);
        BinaryPrimitives.WriteUInt32BigEndian(sector[170..174], 2048);
        sector[181] = 2;
        sector[188] = 1;
    }

    private static int WriteDirectoryRecord(
        Span<byte> directory,
        int offset,
        byte[] identifier,
        uint extent,
        uint length,
        byte flags)
    {
        int recordLength = 33 + identifier.Length + (identifier.Length % 2 == 0 ? 1 : 0);
        Span<byte> record = directory.Slice(offset, recordLength);
        record[0] = (byte)recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(record[2..6], extent);
        BinaryPrimitives.WriteUInt32BigEndian(record[6..10], extent);
        BinaryPrimitives.WriteUInt32LittleEndian(record[10..14], length);
        BinaryPrimitives.WriteUInt32BigEndian(record[14..18], length);
        record[25] = flags;
        record[28] = 1;
        record[31] = 1;
        record[32] = (byte)identifier.Length;
        identifier.CopyTo(record[33..]);
        return recordLength;
    }
}

