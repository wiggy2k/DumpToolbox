using Microsoft.Data.Sqlite;

namespace DumpToolbox.Core;

public sealed partial class SkeletoolCatalogueService
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var db = new SqliteConnection($"Data Source={DatabasePath};Cache=Shared");
        await db.OpenAsync(ct).ConfigureAwait(false);

        // Used only by the one-time v1 -> v2 migration. Keeping the conversion inside
        // SQLite lets the existing catalogue be compacted without re-reading disc images.
        db.CreateFunction<string, byte[]>("sha1_blob", static value => Sha1Bytes(value));

        using (SqliteCommand pragmas = db.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            await pragmas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int version = await ReadSchemaVersionAsync(db, ct).ConfigureAwait(false);
        if (version == 0 && await LooksLikeSchemaV1Async(db, ct).ConfigureAwait(false))
            version = 1;

        if (version == 0)
        {
            await CreateSchemaV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 1)
        {
            await MigrateSchemaV1ToV2Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV2ToV3Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 2)
        {
            await MigrateSchemaV2ToV3Async(db, ct).ConfigureAwait(false);
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version == 3)
        {
            await MigrateSchemaV3ToV4Async(db, ct).ConfigureAwait(false);
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported SkeleTool SHA-1 catalogue schema version {version}.");
        }
        else
        {
            await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
        }

        return db;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand exists = db.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='meta')";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return 0;

        using SqliteCommand version = db.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key='schema_version' LIMIT 1";
        object? value = await version.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null || value is DBNull || !int.TryParse(Convert.ToString(value), out int parsed) ? 0 : parsed;
    }

    private static async Task<bool> LooksLikeSchemaV1Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('files') WHERE name='sha1')";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0;
    }

    private static async Task CreateSchemaV4Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS roots(
 id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, active INTEGER NOT NULL DEFAULT 1,
 added_utc TEXT NOT NULL, last_scanned_utc TEXT, last_success_utc TEXT, last_error TEXT);
CREATE TABLE IF NOT EXISTS units(
 id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id), kind TEXT NOT NULL,
 current_path TEXT NOT NULL, relative_path TEXT NOT NULL, size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
 sha1 BLOB NOT NULL CHECK(length(sha1)=20), layout_hash TEXT NOT NULL DEFAULT '', present INTEGER NOT NULL DEFAULT 1,
 first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, missing_since_utc TEXT, last_scanned_utc TEXT,
 evidence_gathered INTEGER NOT NULL DEFAULT 0, evidence_gathered_utc TEXT, evidence_schema INTEGER);
CREATE TABLE IF NOT EXISTS images(
 id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id) ON DELETE CASCADE,
 entry_path TEXT NOT NULL, display_name TEXT NOT NULL, source_offset INTEGER NOT NULL, source_length INTEGER NOT NULL,
 image_sha1 BLOB NOT NULL CHECK(length(image_sha1)=20), volume_identifier TEXT, image_kind TEXT, scanner_kind TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS hashes(
 id INTEGER PRIMARY KEY, sha1 BLOB NOT NULL CHECK(length(sha1)=20), size INTEGER NOT NULL,
 UNIQUE(sha1,size));
CREATE TABLE IF NOT EXISTS files(
 id INTEGER PRIMARY KEY, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 relative_path TEXT NOT NULL, hash_id INTEGER NOT NULL REFERENCES hashes(id), image_lba INTEGER, image_extents TEXT);
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','4');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaV4IndexesAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_units_root ON units(root_id,present);
CREATE INDEX IF NOT EXISTS ix_units_identity ON units(kind,sha1,layout_hash);
CREATE INDEX IF NOT EXISTS ix_images_unit ON images(unit_id);
CREATE INDEX IF NOT EXISTS ix_files_hash ON files(hash_id,image_id);
CREATE INDEX IF NOT EXISTS ix_files_image ON files(image_id);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaV2IndexesAsync(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_units_root ON units(root_id,present);
CREATE INDEX IF NOT EXISTS ix_units_identity ON units(kind,sha1,layout_hash);
CREATE INDEX IF NOT EXISTS ix_images_unit ON images(unit_id);
CREATE INDEX IF NOT EXISTS ix_files_hash ON files(hash_id,image_id);
CREATE INDEX IF NOT EXISTS ix_files_image ON files(image_id);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task MigrateSchemaV1ToV2Async(SqliteConnection db, CancellationToken ct)
    {
        // The old catalogue may be hundreds of MiB. Migrate in-place from its already
        // calculated hashes, then VACUUM once so the obsolete text/hash index pages are
        // actually returned to the filesystem. No disc/archive content is rescanned.
        using (SqliteCommand fkOff = db.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys=OFF";
            await fkOff.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            using SqliteCommand cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
ALTER TABLE files RENAME TO files_v1;
ALTER TABLE images RENAME TO images_v1;
ALTER TABLE units RENAME TO units_v1;

CREATE TABLE units(
 id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id), kind TEXT NOT NULL,
 current_path TEXT NOT NULL, relative_path TEXT NOT NULL, size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
 sha1 BLOB NOT NULL CHECK(length(sha1)=20), layout_hash TEXT NOT NULL DEFAULT '', present INTEGER NOT NULL DEFAULT 1,
 first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, missing_since_utc TEXT, last_scanned_utc TEXT);
CREATE TABLE images(
 id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id) ON DELETE CASCADE,
 entry_path TEXT NOT NULL, display_name TEXT NOT NULL, source_offset INTEGER NOT NULL, source_length INTEGER NOT NULL,
 image_sha1 BLOB NOT NULL CHECK(length(image_sha1)=20), volume_identifier TEXT, image_kind TEXT, scanner_kind TEXT NOT NULL);
CREATE TABLE hashes(
 id INTEGER PRIMARY KEY, sha1 BLOB NOT NULL CHECK(length(sha1)=20), size INTEGER NOT NULL,
 UNIQUE(sha1,size));
CREATE TABLE files(
 id INTEGER PRIMARY KEY, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
 relative_path TEXT NOT NULL, hash_id INTEGER NOT NULL REFERENCES hashes(id), image_lba INTEGER);

INSERT INTO units(id,root_id,kind,current_path,relative_path,size,mtime_ticks,sha1,layout_hash,present,first_seen_utc,last_seen_utc,missing_since_utc,last_scanned_utc)
 SELECT id,root_id,kind,current_path,relative_path,size,mtime_ticks,sha1_blob(sha1),layout_hash,present,first_seen_utc,last_seen_utc,missing_since_utc,last_scanned_utc FROM units_v1;
INSERT INTO images(id,unit_id,entry_path,display_name,source_offset,source_length,image_sha1,volume_identifier,image_kind,scanner_kind)
 SELECT id,unit_id,entry_path,display_name,source_offset,source_length,sha1_blob(image_sha1),volume_identifier,image_kind,scanner_kind FROM images_v1;
INSERT INTO hashes(sha1,size)
 SELECT DISTINCT sha1_blob(sha1),size FROM files_v1;
INSERT INTO files(id,image_id,relative_path,hash_id,image_lba)
 SELECT f.id,f.image_id,f.relative_path,h.id,f.image_lba
 FROM files_v1 f JOIN hashes h ON h.sha1=sha1_blob(f.sha1) AND h.size=f.size;

DROP TABLE files_v1;
DROP TABLE images_v1;
DROP TABLE units_v1;
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','2');";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EnsureSchemaV2IndexesAsync(db, ct).ConfigureAwait(false);
        using (SqliteCommand fkOn = db.CreateCommand())
        {
            fkOn.CommandText = "PRAGMA foreign_keys=ON";
            await fkOn.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using (SqliteCommand checkpoint = db.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using (SqliteCommand compact = db.CreateCommand())
        {
            compact.CommandText = "VACUUM";
            await compact.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task MigrateSchemaV3ToV4Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE files ADD COLUMN image_extents TEXT;
-- v3 stored only one LBA per ISO9660 file, so multi-extent hashes from those scans
-- cannot be trusted. Force filesystem-image units through one fresh scan; unchanged
-- archive/direct identity is retained, but their image contents will be re-indexed.
UPDATE units SET last_scanned_utc=NULL WHERE id IN (SELECT DISTINCT unit_id FROM images WHERE scanner_kind='ISO9660');
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','4');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }

    private static string SerializeImageExtents(IReadOnlyList<SkeletonSourceImageExtent> extents)
        => string.Join(";", extents.Select(extent => $"{extent.Lba}:{extent.Length}"));

    private static IReadOnlyList<SkeletonSourceImageExtent>? ParseImageExtents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new List<SkeletonSourceImageExtent>();
        foreach (string item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = item.IndexOf(':');
            if (colon <= 0 || !long.TryParse(item[..colon], out long lba) || !long.TryParse(item[(colon + 1)..], out long length) || lba < 0 || length < 0)
                return null;
            result.Add(new SkeletonSourceImageExtent(lba, length));
        }
        return result.Count == 0 ? null : result;
    }

    private static async Task MigrateSchemaV2ToV3Async(SqliteConnection db, CancellationToken ct)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = @"
ALTER TABLE units ADD COLUMN evidence_gathered INTEGER NOT NULL DEFAULT 0;
ALTER TABLE units ADD COLUMN evidence_gathered_utc TEXT;
ALTER TABLE units ADD COLUMN evidence_schema INTEGER;
INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','3');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSchemaV4IndexesAsync(db, ct).ConfigureAwait(false);
    }
}
