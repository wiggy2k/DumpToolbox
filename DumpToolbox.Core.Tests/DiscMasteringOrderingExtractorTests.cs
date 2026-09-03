using System.Buffers.Binary;
using System.Text;
using DumpToolbox.Core;
using Microsoft.Data.Sqlite;

namespace DumpToolbox.Core.Tests;

public sealed class DiscMasteringOrderingExtractorTests
{
    [Fact]
    public async Task ExtractsDescriptorSequenceJolietRecordPositionsAndBothPathTableOrders()
    {
        byte[] pvd = Descriptor(1, string.Empty, "PRIMARY TOOL", 10_150, 25, 2048, 0, 0, 0, 0, []);
        byte[] rootSystemUse = [0x12, 0x34, (byte)'X', (byte)'A'];
        byte[] svd = Descriptor(2, "%/E", "JOLIET TOOL", 10_000, 30, 2048, 44, 40, 0, 41,
            rootSystemUse);
        byte[] terminator = Descriptor(255, string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, 0, []);

        List<DiscVolumeDescriptorEvidence> descriptors = await DiscMasteringOrderingExtractor.ReadDescriptorsAsync(
            (lba, _) => Task.FromResult(lba switch { 16 => pvd, 17 => svd, _ => terminator }), default);

        Assert.Equal(3, descriptors.Count);
        DiscVolumeDescriptorEvidence joliet = descriptors[1];
        Assert.Equal("JOLIET", joliet.Namespace);
        Assert.Equal(17, joliet.DescriptorLba);
        Assert.Equal(1, joliet.DescriptorSequence);
        Assert.Equal("%/E", joliet.EscapeSequence);
        Assert.Equal("JOLIET TOOL", joliet.ApplicationId);
        Assert.Equal(10_000u, joliet.VolumeSpaceSize);
        Assert.Equal(rootSystemUse, joliet.RootSystemUse);

        byte[] directory = new byte[2048];
        int offset = 0;
        offset += WriteDirectoryRecord(directory, offset, [0], 30, 2048, 2);
        offset += WriteDirectoryRecord(directory, offset, [1], 30, 2048, 2);
        offset += WriteDirectoryRecord(directory, offset, Joliet("beta.bin"), 100, 9, 0);
        WriteDirectoryRecord(directory, offset, Joliet("Alpha.bin"), 101, 10, 0);

        List<DiscFilesystemRecordEvidence> records = await DiscMasteringOrderingExtractor.ReadTreeAsync(
            (_, _, _) => Task.FromResult(directory), joliet, default);

        Assert.Equal(["beta.bin", "Alpha.bin"], records.Select(record => record.Identifier));
        Assert.Equal([2, 3], records.Select(record => record.RecordIndex));
        Assert.All(records, record => Assert.Equal(30u, record.DirectoryExtent));
        Assert.Equal(Joliet("beta.bin"), records[0].IdentifierBytes);

        byte[] littlePathTable = PathTable(bigEndian: false);
        byte[] bigPathTable = PathTable(bigEndian: true);
        List<DiscPathTableRecordEvidence> pathRecords = await DiscMasteringOrderingExtractor.ReadPathTablesAsync(
            (lba, _, _) => Task.FromResult(lba == 40 ? littlePathTable : bigPathTable), joliet, default);

        Assert.Equal(6, pathRecords.Count);
        Assert.Equal(["/", "beta", "Alpha"], pathRecords.Where(record => record.TableKind == "L")
            .Select(record => record.Identifier));
        Assert.Equal(["/", "beta", "Alpha"], pathRecords.Where(record => record.TableKind == "M")
            .Select(record => record.Identifier));
        Assert.Equal([1, 2, 3], pathRecords.Where(record => record.TableKind == "L")
            .Select(record => record.DirectoryNumber));
    }

    [Fact]
    public void EvidenceSchemaRequiresExistingUnitsToBeRegathered()
    {
        Assert.Equal(3, DiscEvidenceService.EvidenceSchema);
    }

    [Fact]
    public async Task EvidenceDatabaseMigratesAndCreatesOrderingExports()
    {
        string root = Path.Combine(Path.GetTempPath(), $"DiscOrderingTests_{Guid.NewGuid():N}");
        string database = Path.Combine(root, "evidence.sqlite");
        string exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);
        try
        {
            var service = new DiscEvidenceService(new SkeletoolCatalogueService(), database);
            await service.AnalyseAsync(exports);

            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
                Assert.Equal("2", (string)(await command.ExecuteScalarAsync())!);
                command.CommandText = @"
SELECT COUNT(*) FROM sqlite_master
WHERE type='table' AND name IN ('volume_descriptors','filesystem_records','path_table_records','namespace_record_pairs');";
                Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
            }

            Assert.True(File.Exists(Path.Combine(exports, "volume_descriptor_observations.csv")));
            Assert.True(File.Exists(Path.Combine(exports, "joliet_directory_record_order.csv")));
            Assert.True(File.Exists(Path.Combine(exports, "joliet_path_table_order.csv")));
            Assert.True(File.Exists(Path.Combine(exports, "joliet_iso9660_record_pairs.csv")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Descriptor(byte type, string escapeSequence, string applicationId,
        uint volumeSpaceSize, uint rootExtent, uint rootLength, uint pathTableSize,
        uint typeL, uint optionalL, uint typeM, byte[] rootSystemUse)
    {
        byte[] sector = new byte[2048];
        sector[0] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
        sector[6] = 1;
        Encoding.ASCII.GetBytes(applicationId).CopyTo(sector, 574);
        if (escapeSequence.Length > 0)
            Encoding.ASCII.GetBytes(escapeSequence).CopyTo(sector, 88);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(80, 4), volumeSpaceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(132, 4), pathTableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(140, 4), typeL);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(144, 4), optionalL);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(148, 4), typeM);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(152, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(158, 4), rootExtent);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(166, 4), rootLength);
        int recordLength = 34 + rootSystemUse.Length;
        sector[156] = (byte)recordLength;
        sector[188] = 1;
        sector[189] = 0;
        rootSystemUse.CopyTo(sector, 190);
        return sector;
    }

    private static byte[] PathTable(bool bigEndian)
    {
        var bytes = new byte[44];
        int offset = WritePathTableRecord(bytes, 0, [0], 30, 1, bigEndian);
        offset += WritePathTableRecord(bytes, offset, Joliet("beta"), 31, 1, bigEndian);
        WritePathTableRecord(bytes, offset, Joliet("Alpha"), 32, 1, bigEndian);
        return bytes;
    }

    private static int WritePathTableRecord(byte[] target, int offset, byte[] identifier, uint extent,
        ushort parent, bool bigEndian)
    {
        target[offset] = (byte)identifier.Length;
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset + 2, 4), extent);
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset + 6, 2), parent);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset + 2, 4), extent);
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset + 6, 2), parent);
        }
        identifier.CopyTo(target, offset + 8);
        return 8 + identifier.Length + (identifier.Length & 1);
    }

    private static int WriteDirectoryRecord(byte[] target, int offset, byte[] identifier, uint extent,
        uint length, byte flags)
    {
        int recordLength = 33 + identifier.Length + (identifier.Length % 2 == 0 ? 1 : 0);
        target[offset] = (byte)recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset + 2, 4), extent);
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset + 10, 4), length);
        target[offset + 25] = flags;
        target[offset + 32] = (byte)identifier.Length;
        identifier.CopyTo(target, offset + 33);
        return recordLength;
    }

    private static byte[] Joliet(string value) => Encoding.BigEndianUnicode.GetBytes(value);
}
