using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DumpToolbox.Core;

public sealed partial class DiscEvidenceService
{
    private const int EvidenceDatabaseSchema = 2;

    private static async Task EnsureOrderingSchemaAsync(SqliteConnection db, CancellationToken cancellationToken)
    {
        int version;
        using (SqliteCommand read = db.CreateCommand())
        {
            read.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            string? value = Convert.ToString(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                throw new InvalidOperationException($"Unsupported disc evidence database schema '{value}'.");
        }
        if (version > EvidenceDatabaseSchema)
            throw new InvalidOperationException($"Disc evidence database schema {version} is newer than this application supports.");

        if (version < 2)
        {
            using SqliteCommand migrate = db.CreateCommand();
            migrate.CommandText = @"
CREATE TABLE IF NOT EXISTS volume_descriptors(
 id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 descriptor_sequence INTEGER NOT NULL,descriptor_lba INTEGER NOT NULL,descriptor_type INTEGER NOT NULL,
 namespace TEXT NOT NULL,system_id TEXT NOT NULL,volume_id TEXT NOT NULL,publisher_id TEXT NOT NULL,
 data_preparer_id TEXT NOT NULL,application_id TEXT NOT NULL,volume_space_size INTEGER NOT NULL,
 escape_sequence TEXT NOT NULL,path_table_size INTEGER NOT NULL,type_l_lba INTEGER NOT NULL,
 optional_type_l_lba INTEGER NOT NULL,type_m_lba INTEGER NOT NULL,optional_type_m_lba INTEGER NOT NULL,
 root_extent INTEGER NOT NULL,root_length INTEGER NOT NULL,root_record_length INTEGER NOT NULL,
 root_system_use BLOB NOT NULL,UNIQUE(image_id,descriptor_sequence));
CREATE TABLE IF NOT EXISTS filesystem_records(
 id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 namespace TEXT NOT NULL,path TEXT NOT NULL,parent_path TEXT NOT NULL,identifier TEXT NOT NULL,
 identifier_bytes BLOB NOT NULL,extent INTEGER NOT NULL,length INTEGER NOT NULL,flags INTEGER NOT NULL,
 is_directory INTEGER NOT NULL,directory_extent INTEGER NOT NULL,record_offset INTEGER NOT NULL,
 record_index INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS path_table_records(
 id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 namespace TEXT NOT NULL,table_kind TEXT NOT NULL,table_lba INTEGER NOT NULL,record_index INTEGER NOT NULL,
 record_offset INTEGER NOT NULL,directory_number INTEGER NOT NULL,parent_directory_number INTEGER NOT NULL,
 extent INTEGER NOT NULL,identifier TEXT NOT NULL,identifier_bytes BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS namespace_record_pairs(
 id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 iso_path TEXT NOT NULL,joliet_path TEXT NOT NULL,extent INTEGER NOT NULL,length INTEGER NOT NULL,flags INTEGER NOT NULL,
 iso_directory_extent INTEGER NOT NULL,iso_record_offset INTEGER NOT NULL,iso_record_index INTEGER NOT NULL,
 joliet_directory_extent INTEGER NOT NULL,joliet_record_offset INTEGER NOT NULL,joliet_record_index INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_volume_descriptors_image ON volume_descriptors(image_id,descriptor_sequence);
CREATE INDEX IF NOT EXISTS ix_filesystem_order ON filesystem_records(image_id,namespace,directory_extent,record_index);
CREATE INDEX IF NOT EXISTS ix_path_table_order ON path_table_records(image_id,namespace,table_kind,record_index);
CREATE INDEX IF NOT EXISTS ix_namespace_pairs_image ON namespace_record_pairs(image_id,iso_directory_extent,iso_record_index);
UPDATE meta SET value='2' WHERE key='schema_version';";
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StoreOrderingEvidenceAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long imageId,
        ImageEvidence evidence,
        CancellationToken cancellationToken)
    {
        foreach (DiscVolumeDescriptorEvidence descriptor in evidence.Descriptors)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO volume_descriptors(image_id,descriptor_sequence,descriptor_lba,descriptor_type,namespace,system_id,
 volume_id,publisher_id,data_preparer_id,application_id,volume_space_size,escape_sequence,path_table_size,
 type_l_lba,optional_type_l_lba,type_m_lba,optional_type_m_lba,root_extent,root_length,root_record_length,
 root_system_use)
VALUES($image,$sequence,$lba,$type,$namespace,$system,$volume,$publisher,$preparer,$application,$space,$escape,
 $pathSize,$typeL,$optionalL,$typeM,$optionalM,$rootExtent,$rootLength,$rootRecordLength,$rootSystemUse);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$sequence", descriptor.DescriptorSequence);
            command.Parameters.AddWithValue("$lba", descriptor.DescriptorLba);
            command.Parameters.AddWithValue("$type", descriptor.DescriptorType);
            command.Parameters.AddWithValue("$namespace", descriptor.Namespace);
            command.Parameters.AddWithValue("$system", descriptor.SystemId);
            command.Parameters.AddWithValue("$volume", descriptor.VolumeId);
            command.Parameters.AddWithValue("$publisher", descriptor.PublisherId);
            command.Parameters.AddWithValue("$preparer", descriptor.DataPreparerId);
            command.Parameters.AddWithValue("$application", descriptor.ApplicationId);
            command.Parameters.AddWithValue("$space", descriptor.VolumeSpaceSize);
            command.Parameters.AddWithValue("$escape", descriptor.EscapeSequence);
            command.Parameters.AddWithValue("$pathSize", descriptor.PathTableSize);
            command.Parameters.AddWithValue("$typeL", descriptor.TypeLPathTableLba);
            command.Parameters.AddWithValue("$optionalL", descriptor.OptionalTypeLPathTableLba);
            command.Parameters.AddWithValue("$typeM", descriptor.TypeMPathTableLba);
            command.Parameters.AddWithValue("$optionalM", descriptor.OptionalTypeMPathTableLba);
            command.Parameters.AddWithValue("$rootExtent", descriptor.RootExtent);
            command.Parameters.AddWithValue("$rootLength", descriptor.RootLength);
            command.Parameters.AddWithValue("$rootRecordLength", descriptor.RootRecordLength);
            command.Parameters.AddWithValue("$rootSystemUse", descriptor.RootSystemUse);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (DiscFilesystemRecordEvidence record in evidence.Iso.Concat(evidence.JolietRecords))
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO filesystem_records(image_id,namespace,path,parent_path,identifier,identifier_bytes,extent,length,flags,
 is_directory,directory_extent,record_offset,record_index)
VALUES($image,$namespace,$path,$parent,$identifier,$identifierBytes,$extent,$length,$flags,$directory,
 $directoryExtent,$recordOffset,$recordIndex);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$namespace", record.Namespace);
            command.Parameters.AddWithValue("$path", record.Path);
            command.Parameters.AddWithValue("$parent", record.ParentPath);
            command.Parameters.AddWithValue("$identifier", record.Identifier);
            command.Parameters.AddWithValue("$identifierBytes", record.IdentifierBytes);
            command.Parameters.AddWithValue("$extent", record.Extent);
            command.Parameters.AddWithValue("$length", record.Length);
            command.Parameters.AddWithValue("$flags", record.Flags);
            command.Parameters.AddWithValue("$directory", record.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$directoryExtent", record.DirectoryExtent);
            command.Parameters.AddWithValue("$recordOffset", record.RecordOffset);
            command.Parameters.AddWithValue("$recordIndex", record.RecordIndex);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (DiscPathTableRecordEvidence record in evidence.PathTables)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO path_table_records(image_id,namespace,table_kind,table_lba,record_index,record_offset,
 directory_number,parent_directory_number,extent,identifier,identifier_bytes)
VALUES($image,$namespace,$kind,$lba,$recordIndex,$recordOffset,$directoryNumber,$parentNumber,$extent,
 $identifier,$identifierBytes);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$namespace", record.Namespace);
            command.Parameters.AddWithValue("$kind", record.TableKind);
            command.Parameters.AddWithValue("$lba", record.TableLba);
            command.Parameters.AddWithValue("$recordIndex", record.RecordIndex);
            command.Parameters.AddWithValue("$recordOffset", record.RecordOffset);
            command.Parameters.AddWithValue("$directoryNumber", record.DirectoryNumber);
            command.Parameters.AddWithValue("$parentNumber", record.ParentDirectoryNumber);
            command.Parameters.AddWithValue("$extent", record.Extent);
            command.Parameters.AddWithValue("$identifier", record.Identifier);
            command.Parameters.AddWithValue("$identifierBytes", record.IdentifierBytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (NamePair pair in evidence.Pairs)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO namespace_record_pairs(image_id,iso_path,joliet_path,extent,length,flags,iso_directory_extent,
 iso_record_offset,iso_record_index,joliet_directory_extent,joliet_record_offset,joliet_record_index)
VALUES($image,$isoPath,$jolietPath,$extent,$length,$flags,$isoDirectoryExtent,$isoRecordOffset,$isoRecordIndex,
 $jolietDirectoryExtent,$jolietRecordOffset,$jolietRecordIndex);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$isoPath", pair.IsoPath);
            command.Parameters.AddWithValue("$jolietPath", pair.JolietPath);
            command.Parameters.AddWithValue("$extent", pair.Extent);
            command.Parameters.AddWithValue("$length", pair.Length);
            command.Parameters.AddWithValue("$flags", pair.Flags);
            command.Parameters.AddWithValue("$isoDirectoryExtent", pair.IsoDirectoryExtent);
            command.Parameters.AddWithValue("$isoRecordOffset", pair.IsoRecordOffset);
            command.Parameters.AddWithValue("$isoRecordIndex", pair.IsoRecordIndex);
            command.Parameters.AddWithValue("$jolietDirectoryExtent", pair.JolietDirectoryExtent);
            command.Parameters.AddWithValue("$jolietRecordOffset", pair.JolietRecordOffset);
            command.Parameters.AddWithValue("$jolietRecordIndex", pair.JolietRecordIndex);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> ExportOrderingEvidenceAsync(
        SqliteConnection db,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        await ExportAsync("volume_descriptor_observations.csv",
            "Source,Image,Media,Sequence,LBA,Type,Namespace,SystemId,VolumeId,PublisherId,DataPreparerId,ApplicationId,VolumeSpaceSize,EscapeSequence,PathTableSize,TypeLLBA,OptionalTypeLLBA,TypeMLBA,OptionalTypeMLBA,RootExtent,RootLength,RootRecordLength,RootSystemUseHex",
            @"SELECT s.source_path,i.display_name,i.media_type,v.descriptor_sequence,v.descriptor_lba,v.descriptor_type,
v.namespace,v.system_id,v.volume_id,v.publisher_id,v.data_preparer_id,v.application_id,v.volume_space_size,
v.escape_sequence,v.path_table_size,v.type_l_lba,v.optional_type_l_lba,v.type_m_lba,v.optional_type_m_lba,
v.root_extent,v.root_length,v.root_record_length,hex(v.root_system_use)
FROM volume_descriptors v JOIN images i ON i.id=v.image_id
LEFT JOIN scans s ON s.catalogue_unit_id=i.catalogue_unit_id
ORDER BY s.source_path,i.display_name,v.descriptor_sequence;").ConfigureAwait(false);
        await ExportAsync("joliet_directory_record_order.csv",
            "Source,Image,ApplicationId,EscapeSequence,ParentPath,DirectoryExtent,RecordIndex,RecordOffset,Path,Identifier,IdentifierBytesHex,Extent,Length,Flags,IsDirectory",
            @"SELECT s.source_path,i.display_name,COALESCE(v.application_id,''),COALESCE(v.escape_sequence,''),
f.parent_path,f.directory_extent,f.record_index,f.record_offset,f.path,f.identifier,hex(f.identifier_bytes),
f.extent,f.length,f.flags,f.is_directory
FROM filesystem_records f JOIN images i ON i.id=f.image_id
LEFT JOIN scans s ON s.catalogue_unit_id=i.catalogue_unit_id
LEFT JOIN volume_descriptors v ON v.id=(SELECT id FROM volume_descriptors candidate
 WHERE candidate.image_id=i.id AND candidate.namespace='JOLIET' ORDER BY candidate.descriptor_sequence LIMIT 1)
WHERE f.namespace='JOLIET'
ORDER BY s.source_path,i.display_name,f.directory_extent,f.record_index;").ConfigureAwait(false);
        await ExportAsync("joliet_path_table_order.csv",
            "Source,Image,ApplicationId,EscapeSequence,TableKind,TableLBA,RecordIndex,RecordOffset,DirectoryNumber,ParentDirectoryNumber,Extent,Identifier,IdentifierBytesHex",
            @"SELECT s.source_path,i.display_name,COALESCE(v.application_id,''),COALESCE(v.escape_sequence,''),
p.table_kind,p.table_lba,p.record_index,p.record_offset,p.directory_number,p.parent_directory_number,p.extent,
p.identifier,hex(p.identifier_bytes)
FROM path_table_records p JOIN images i ON i.id=p.image_id
LEFT JOIN scans s ON s.catalogue_unit_id=i.catalogue_unit_id
LEFT JOIN volume_descriptors v ON v.id=(SELECT id FROM volume_descriptors candidate
 WHERE candidate.image_id=i.id AND candidate.namespace='JOLIET' ORDER BY candidate.descriptor_sequence LIMIT 1)
WHERE p.namespace='JOLIET'
ORDER BY s.source_path,i.display_name,p.table_kind,p.record_index;").ConfigureAwait(false);
        await ExportAsync("joliet_iso9660_record_pairs.csv",
            "Source,Image,ApplicationId,ISOPath,JolietPath,Extent,Length,Flags,ISODirectoryExtent,ISORecordIndex,ISORecordOffset,JolietDirectoryExtent,JolietRecordIndex,JolietRecordOffset",
            @"SELECT s.source_path,i.display_name,COALESCE(v.application_id,''),p.iso_path,p.joliet_path,p.extent,
p.length,p.flags,p.iso_directory_extent,p.iso_record_index,p.iso_record_offset,p.joliet_directory_extent,
p.joliet_record_index,p.joliet_record_offset
FROM namespace_record_pairs p JOIN images i ON i.id=p.image_id
LEFT JOIN scans s ON s.catalogue_unit_id=i.catalogue_unit_id
LEFT JOIN volume_descriptors v ON v.id=(SELECT id FROM volume_descriptors candidate
 WHERE candidate.image_id=i.id AND candidate.namespace='JOLIET' ORDER BY candidate.descriptor_sequence LIMIT 1)
ORDER BY s.source_path,i.display_name,p.iso_directory_extent,p.iso_record_index;").ConfigureAwait(false);
        return paths;

        async Task ExportAsync(string filename, string header, string sql)
        {
            string path = Path.Combine(outputDirectory, filename);
            paths.Add(path);
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteLineAsync(header).ConfigureAwait(false);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = sql;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string line = string.Join(',', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Csv(reader.IsDBNull(index)
                        ? string.Empty
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty)));
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
    }
}
