using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Text;

namespace DumpToolbox.Core;

public sealed record DiscEvidenceProgress(string Phase, string Source, int Completed, int Total, int Images, int Errors);

public sealed class DiscEvidenceService
{
    public const int EvidenceSchema = 2;
    private readonly SkeletoolCatalogueService _catalogue;
    public string DatabasePath { get; } = Path.Combine(AppContext.BaseDirectory, "disc_mastering_evidence.sqlite");

    public DiscEvidenceService(SkeletoolCatalogueService catalogue) => _catalogue = catalogue;

    public async Task<(int Pending,int Complete)> GetQueueStatsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SkeletoolEvidenceUnit> pending = await _catalogue.GetPendingEvidenceUnitsAsync(EvidenceSchema, ct).ConfigureAwait(false);
        await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT catalogue_unit_id) FROM scans WHERE status IN ('complete','skipped')";
        int complete = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return (pending.Count, complete);
    }

    public async Task ScanPendingAsync(int workerCount, IProgress<DiscEvidenceProgress>? progress = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        IReadOnlyList<SkeletoolEvidenceUnit> units = await _catalogue.GetPendingEvidenceUnitsAsync(EvidenceSchema, ct).ConfigureAwait(false);
        int total = units.Count, done = 0, images = 0, errors = 0;
        log?.Report($"Evidence scan: {total:N0} pending catalogue unit(s); workers={Math.Clamp(workerCount,1,64)}; database={DatabasePath}");
        if (total == 0) { progress?.Report(new("Complete", string.Empty, 0, 0, 0, 0)); return; }

        await Parallel.ForEachAsync(units, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(workerCount,1,64), CancellationToken = ct }, async (unit, token) =>
        {
            progress?.Report(new("Scanning", unit.SourcePath, Volatile.Read(ref done), total, Volatile.Read(ref images), Volatile.Read(ref errors)));
            try
            {
                log?.Report($"UNIT {unit.Id}: {unit.SourcePath}");
                IReadOnlyList<SkeletoolEvidenceImage> unitImages = await _catalogue.GetEvidenceImagesAsync(unit.Id, token).ConfigureAwait(false);
                if (unitImages.Count == 0)
                {
                    await StoreUnitStatusAsync(unit, "skipped", "No catalogue data images.", token).ConfigureAwait(false);
                    await _catalogue.MarkEvidenceGatheredAsync(unit.Id, EvidenceSchema, token).ConfigureAwait(false);
                    log?.Report($"SKIP {unit.SourcePath}: no data images");
                }
                else
                {
                    int unitErrors = 0;
                    foreach (SkeletoolEvidenceImage image in unitImages)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            string path = await _catalogue.MaterializeEvidenceImageAsync(image, token).ConfigureAwait(false);
                            log?.Report($"  IMAGE {image.DisplayName}: {image.SourceLength:N0} bytes");
                            ImageEvidence evidence = await InspectImageAsync(path, image, log, token).ConfigureAwait(false);
                            await StoreImageEvidenceAsync(unit, image, evidence, token).ConfigureAwait(false);
                            Interlocked.Increment(ref images);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            unitErrors++; Interlocked.Increment(ref errors);
                            log?.Report($"  ERROR {image.DisplayName}: {ex.GetType().Name}: {ex.Message}");
                            await StoreImageErrorAsync(unit, image, ex.Message, token).ConfigureAwait(false);
                        }
                    }
                    if (unitErrors == 0)
                    {
                        await StoreUnitStatusAsync(unit, "complete", null, token).ConfigureAwait(false);
                        await _catalogue.MarkEvidenceGatheredAsync(unit.Id, EvidenceSchema, token).ConfigureAwait(false);
                        log?.Report($"DONE {unit.SourcePath}");
                    }
                    else
                        await StoreUnitStatusAsync(unit, "error", $"{unitErrors} image error(s)", token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                log?.Report($"ERROR {unit.SourcePath}: {ex.GetType().Name}: {ex.Message}");
                await StoreUnitStatusAsync(unit, "error", ex.Message, token).ConfigureAwait(false);
            }
            int finished = Interlocked.Increment(ref done);
            progress?.Report(new("Scanning", unit.SourcePath, finished, total, Volatile.Read(ref images), Volatile.Read(ref errors)));
        }).ConfigureAwait(false);
        progress?.Report(new("Complete", string.Empty, done, total, images, errors));
        log?.Report($"Evidence scan complete: units={done:N0}, images={images:N0}, errors={errors:N0}");
    }

    public async Task AnalyseAsync(string outputDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await using SqliteConnection db = await OpenAsync(ct).ConfigureAwait(false);
        string names = Path.Combine(outputDirectory, "joliet_iso9660_observations.csv");
        await using (var w = new StreamWriter(names, false, new UTF8Encoding(false)))
        {
            await w.WriteLineAsync("SystemId,ApplicationId,DataPreparerId,PublisherId,Media,ISOPath,JolietPath,Extent,Length,Flags");
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT d.system_id,d.application_id,d.data_preparer_id,d.publisher_id,i.media_type,n.iso_path,n.joliet_path,n.extent,n.length,n.flags FROM name_pairs n JOIN images i ON i.id=n.image_id JOIN descriptors d ON d.image_id=i.id AND d.namespace='ISO9660' ORDER BY d.application_id,n.iso_path";
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                await w.WriteLineAsync(string.Join(',', Enumerable.Range(0,10).Select(i => Csv(r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i))!))));
        }
        string eof = Path.Combine(outputDirectory, "eof_slack_observations.csv");
        await using (var w = new StreamWriter(eof, false, new UTF8Encoding(false)))
        {
            await w.WriteLineAsync("SystemId,ApplicationId,DataPreparerId,PublisherId,Media,Path,Extent,Length,TailOffset,TailLength,Status,NonZeroBytes,MatchCount,DeltaSectors");
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT d.system_id,d.application_id,d.data_preparer_id,d.publisher_id,i.media_type,e.path,e.extent,e.length,e.tail_offset,e.tail_length,e.status,e.nonzero_bytes,e.match_count,e.delta_sectors FROM eof_observations e JOIN images i ON i.id=e.image_id JOIN descriptors d ON d.image_id=i.id AND d.namespace='ISO9660' ORDER BY d.application_id,e.path";
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                await w.WriteLineAsync(string.Join(',', Enumerable.Range(0,14).Select(i => Csv(r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i))!))));
        }
        log?.Report($"Analysis exports written: {names}; {eof}");
    }

    private async Task<ImageEvidence> InspectImageAsync(string path, SkeletoolEvidenceImage info, IProgress<string>? log, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024*1024, FileOptions.Asynchronous|FileOptions.RandomAccess);
        SectorReader reader = await SectorReader.DetectAsync(fs, ct).ConfigureAwait(false);
        long logicalSectors = fs.Length / reader.PhysicalSectorSize;
        bool dvdSized = logicalSectors > 450_000 || fs.Length > 900L*1024*1024;
        bool udf = await DetectUdfAsync(reader, ct).ConfigureAwait(false);
        VolumeDescriptor? primary = null, joliet = null;
        for (long lba=16; lba<80; lba++)
        {
            byte[] s = await reader.ReadAsync(lba, ct).ConfigureAwait(false);
            if (Encoding.ASCII.GetString(s,1,5) != "CD001") continue;
            byte type=s[0];
            if (type==1) primary = ParseDescriptor(s, "ISO9660");
            else if (type==2 && s[88]==0x25 && s[89]==0x2F) joliet = ParseDescriptor(s, "JOLIET");
            else if (type==255) break;
        }
        string media = dvdSized || udf ? "DVD" : "CD";
        if (primary is null)
        {
            log?.Report($"    media={media}; ISO9660 PVD not found; UDF={(udf?"yes":"no")}");
            return new(media, udf, null, null, [], [], []);
        }
        List<FsRecord> iso = await ReadTreeAsync(reader, primary.RootExtent, primary.RootLength, false, ct).ConfigureAwait(false);
        List<FsRecord> jol = joliet is null ? [] : await ReadTreeAsync(reader, joliet.RootExtent, joliet.RootLength, true, ct).ConfigureAwait(false);
        var jolGroups = jol.GroupBy(x => (x.Extent,x.Length,x.IsDirectory)).ToDictionary(g=>g.Key,g=>g.ToList());
        var pairs = new List<NamePair>();
        foreach (FsRecord r in iso)
            if (jolGroups.TryGetValue((r.Extent,r.Length,r.IsDirectory), out var matches))
                foreach (FsRecord j in matches) pairs.Add(new(r.Path,j.Path,r.Extent,r.Length,r.Flags));
        var eofs = new List<EofObservation>();
        var pendingTails = new List<PendingTail>();
        foreach (FsRecord f in iso.Where(x=>!x.IsDirectory && x.Length>0))
        {
            int rem=(int)(f.Length%2048);
            if(rem==0){ eofs.Add(new(f.Path,f.Extent,f.Length,0,0,"SECTOR_ALIGNED",0,0,"")); continue; }
            long finalLba=f.Extent+(f.Length/2048);
            byte[] sector=await reader.ReadAsync(finalLba,ct).ConfigureAwait(false);
            byte[] tail=sector[rem..];
            int nz=tail.Count(b=>b!=0);
            if(nz==0){ eofs.Add(new(f.Path,f.Extent,f.Length,rem,tail.Length,"ZERO_SLACK",0,0,"")); continue; }
            pendingTails.Add(new PendingTail(f.Path,f.Extent,f.Length,finalLba,rem,tail,nz,new List<long>()));
        }
        if (pendingTails.Count > 0)
        {
            long maxLba = pendingTails.Max(x => x.FinalLba);
            log?.Report($"    EOF: {pendingTails.Count:N0} non-zero tail(s); one-pass earlier-sector search through LBA {maxLba:N0}");
            for (long l=0; l<maxLba; l++)
            {
                ct.ThrowIfCancellationRequested();
                byte[] earlier=await reader.ReadAsync(l,ct).ConfigureAwait(false);
                foreach (PendingTail t in pendingTails)
                    if (l < t.FinalLba && earlier.AsSpan(t.Offset,t.Bytes.Length).SequenceEqual(t.Bytes))
                        t.Deltas.Add(t.FinalLba-l);
            }
            foreach (PendingTail t in pendingTails)
                eofs.Add(new(t.Path,t.Extent,t.Length,t.Offset,t.Bytes.Length,t.Deltas.Count>0?"NONZERO_MATCHED":"NONZERO_UNMATCHED",t.NonZeroBytes,t.Deltas.Count,string.Join('|',t.Deltas)));
        }
        log?.Report($"    media={media}; udf={(udf?"yes":"no")}; ISO={iso.Count:N0}; Joliet={jol.Count:N0}; pairs={pairs.Count:N0}; EOF={eofs.Count:N0}");
        return new(media,udf,primary,joliet,iso,pairs,eofs);
    }

    private static async Task<bool> DetectUdfAsync(SectorReader reader, CancellationToken ct)
    {
        for(long lba=16;lba<32;lba++)
        {
            byte[] s=await reader.ReadAsync(lba,ct).ConfigureAwait(false);
            string id=Encoding.ASCII.GetString(s,1,5);
            if(id is "NSR02" or "NSR03") return true;
        }
        return false;
    }

    private static VolumeDescriptor ParseDescriptor(byte[] s,string ns)
    {
        string A(int o,int n)=>Encoding.ASCII.GetString(s,o,n).TrimEnd('\0',' ');
        uint rootExtent=BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(158,4));
        uint rootLength=BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(166,4));
        return new(ns,A(8,32),A(40,32),A(318,128),A(446,128),A(574,128),rootExtent,rootLength);
    }

    private static async Task<List<FsRecord>> ReadTreeAsync(SectorReader reader,uint rootExtent,uint rootLength,bool joliet,CancellationToken ct)
    {
        var result=new List<FsRecord>(); var seen=new HashSet<uint>();
        async Task Walk(uint extent,uint length,string parent)
        {
            if(!seen.Add(extent)) return;
            byte[] data=await reader.ReadBytesAsync(extent,length,ct).ConfigureAwait(false);
            int p=0;
            while(p<data.Length)
            {
                int len=data[p]; if(len==0){p=((p/2048)+1)*2048;continue;} if(p+len>data.Length)break;
                if(len<34){p+=len;continue;}
                uint ex=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p+2,4)); uint sz=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p+10,4)); byte flags=data[p+25]; int nl=data[p+32];
                if(33+nl>len){p+=len;continue;}
                if(nl==1 && (data[p+33]==0 || data[p+33]==1)){p+=len;continue;}
                byte[] nameBytes = data.AsSpan(p+33,nl).ToArray();
                string name=joliet?DecodeJoliet(nameBytes):Encoding.ASCII.GetString(nameBytes); int semi=name.LastIndexOf(';'); if(semi>=0)name=name[..semi];
                string path=parent=="/"?"/"+name:parent+"/"+name; bool dir=(flags&2)!=0;
                result.Add(new(path,ex,sz,flags,dir)); if(dir && sz>0) await Walk(ex,sz,path).ConfigureAwait(false); p+=len;
            }
        }
        await Walk(rootExtent,rootLength,"/").ConfigureAwait(false); return result;
    }
    private static string DecodeJoliet(ReadOnlySpan<byte> b){ var chars=new char[b.Length/2]; for(int i=0;i<chars.Length;i++) chars[i]=(char)((b[i*2]<<8)|b[i*2+1]); return new string(chars).TrimEnd('\0'); }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var db=new SqliteConnection($"Data Source={DatabasePath};Cache=Shared;Default Timeout=30"); await db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd=db.CreateCommand(); cmd.CommandText=@"PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY,catalogue_unit_id INTEGER NOT NULL,source_path TEXT NOT NULL,unit_sha1 TEXT NOT NULL,status TEXT NOT NULL,error TEXT,scanned_utc TEXT NOT NULL,UNIQUE(catalogue_unit_id));
CREATE TABLE IF NOT EXISTS images(id INTEGER PRIMARY KEY,catalogue_image_id INTEGER NOT NULL UNIQUE,catalogue_unit_id INTEGER NOT NULL,display_name TEXT,entry_path TEXT,media_type TEXT,udf_present INTEGER NOT NULL DEFAULT 0,status TEXT,error TEXT);
CREATE TABLE IF NOT EXISTS descriptors(id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,namespace TEXT NOT NULL,system_id TEXT,volume_id TEXT,publisher_id TEXT,data_preparer_id TEXT,application_id TEXT,root_extent INTEGER,root_length INTEGER);
CREATE TABLE IF NOT EXISTS name_pairs(id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,iso_path TEXT NOT NULL,joliet_path TEXT NOT NULL,extent INTEGER,length INTEGER,flags INTEGER);
CREATE TABLE IF NOT EXISTS eof_observations(id INTEGER PRIMARY KEY,image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,path TEXT NOT NULL,extent INTEGER,length INTEGER,tail_offset INTEGER,tail_length INTEGER,status TEXT,nonzero_bytes INTEGER,match_count INTEGER,delta_sectors TEXT);
CREATE INDEX IF NOT EXISTS ix_name_pairs_image ON name_pairs(image_id); CREATE INDEX IF NOT EXISTS ix_eof_image ON eof_observations(image_id); INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','1');";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); return db;
    }

    private async Task StoreUnitStatusAsync(SkeletoolEvidenceUnit unit,string status,string? error,CancellationToken ct)
    { await using var db=await OpenAsync(ct); using var cmd=db.CreateCommand(); cmd.CommandText=@"INSERT INTO scans(catalogue_unit_id,source_path,unit_sha1,status,error,scanned_utc) VALUES($u,$p,$h,$s,$e,$n) ON CONFLICT(catalogue_unit_id) DO UPDATE SET source_path=excluded.source_path,unit_sha1=excluded.unit_sha1,status=excluded.status,error=excluded.error,scanned_utc=excluded.scanned_utc"; cmd.Parameters.AddWithValue("$u",unit.Id);cmd.Parameters.AddWithValue("$p",unit.SourcePath);cmd.Parameters.AddWithValue("$h",unit.Sha1);cmd.Parameters.AddWithValue("$s",status);cmd.Parameters.AddWithValue("$e",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$n",DateTimeOffset.UtcNow.ToString("O")); await cmd.ExecuteNonQueryAsync(ct); }

    private async Task StoreImageErrorAsync(SkeletoolEvidenceUnit unit,SkeletoolEvidenceImage image,string error,CancellationToken ct)
    { await using var db=await OpenAsync(ct); using var cmd=db.CreateCommand(); cmd.CommandText=@"INSERT INTO images(catalogue_image_id,catalogue_unit_id,display_name,entry_path,media_type,udf_present,status,error) VALUES($i,$u,$d,$e,'UNKNOWN',0,'error',$x) ON CONFLICT(catalogue_image_id) DO UPDATE SET status='error',error=excluded.error";cmd.Parameters.AddWithValue("$i",image.Id);cmd.Parameters.AddWithValue("$u",unit.Id);cmd.Parameters.AddWithValue("$d",image.DisplayName);cmd.Parameters.AddWithValue("$e",image.EntryPath);cmd.Parameters.AddWithValue("$x",error);await cmd.ExecuteNonQueryAsync(ct); }

    private async Task StoreImageEvidenceAsync(SkeletoolEvidenceUnit unit,SkeletoolEvidenceImage image,ImageEvidence ev,CancellationToken ct)
    {
        await using var db=await OpenAsync(ct); await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        using var up=db.CreateCommand();up.Transaction=tx;up.CommandText=@"INSERT INTO images(catalogue_image_id,catalogue_unit_id,display_name,entry_path,media_type,udf_present,status,error) VALUES($i,$u,$d,$e,$m,$f,'complete',NULL) ON CONFLICT(catalogue_image_id) DO UPDATE SET media_type=excluded.media_type,udf_present=excluded.udf_present,status='complete',error=NULL RETURNING id";up.Parameters.AddWithValue("$i",image.Id);up.Parameters.AddWithValue("$u",unit.Id);up.Parameters.AddWithValue("$d",image.DisplayName);up.Parameters.AddWithValue("$e",image.EntryPath);up.Parameters.AddWithValue("$m",ev.Media);up.Parameters.AddWithValue("$f",ev.UdfPresent?1:0);long imageId=Convert.ToInt64(await up.ExecuteScalarAsync(ct));
        foreach(string table in new[]{"descriptors","name_pairs","eof_observations"}){using var del=db.CreateCommand();del.Transaction=tx;del.CommandText=$"DELETE FROM {table} WHERE image_id=$i";del.Parameters.AddWithValue("$i",imageId);await del.ExecuteNonQueryAsync(ct);}
        foreach(var d in new[]{ev.Primary,ev.Joliet}.Where(x=>x is not null).Cast<VolumeDescriptor>()){using var c=db.CreateCommand();c.Transaction=tx;c.CommandText=@"INSERT INTO descriptors(image_id,namespace,system_id,volume_id,publisher_id,data_preparer_id,application_id,root_extent,root_length) VALUES($i,$n,$s,$v,$p,$d,$a,$r,$l)";c.Parameters.AddWithValue("$i",imageId);c.Parameters.AddWithValue("$n",d.Namespace);c.Parameters.AddWithValue("$s",d.SystemId);c.Parameters.AddWithValue("$v",d.VolumeId);c.Parameters.AddWithValue("$p",d.PublisherId);c.Parameters.AddWithValue("$d",d.DataPreparerId);c.Parameters.AddWithValue("$a",d.ApplicationId);c.Parameters.AddWithValue("$r",d.RootExtent);c.Parameters.AddWithValue("$l",d.RootLength);await c.ExecuteNonQueryAsync(ct);}
        foreach(var n in ev.Pairs){using var c=db.CreateCommand();c.Transaction=tx;c.CommandText=@"INSERT INTO name_pairs(image_id,iso_path,joliet_path,extent,length,flags) VALUES($i,$a,$b,$e,$l,$f)";c.Parameters.AddWithValue("$i",imageId);c.Parameters.AddWithValue("$a",n.IsoPath);c.Parameters.AddWithValue("$b",n.JolietPath);c.Parameters.AddWithValue("$e",n.Extent);c.Parameters.AddWithValue("$l",n.Length);c.Parameters.AddWithValue("$f",n.Flags);await c.ExecuteNonQueryAsync(ct);}
        foreach(var e in ev.Eofs){using var c=db.CreateCommand();c.Transaction=tx;c.CommandText=@"INSERT INTO eof_observations(image_id,path,extent,length,tail_offset,tail_length,status,nonzero_bytes,match_count,delta_sectors) VALUES($i,$p,$e,$l,$o,$t,$s,$n,$m,$d)";c.Parameters.AddWithValue("$i",imageId);c.Parameters.AddWithValue("$p",e.Path);c.Parameters.AddWithValue("$e",e.Extent);c.Parameters.AddWithValue("$l",e.Length);c.Parameters.AddWithValue("$o",e.TailOffset);c.Parameters.AddWithValue("$t",e.TailLength);c.Parameters.AddWithValue("$s",e.Status);c.Parameters.AddWithValue("$n",e.NonZeroBytes);c.Parameters.AddWithValue("$m",e.MatchCount);c.Parameters.AddWithValue("$d",e.DeltaSectors);await c.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);
    }

    private static string Csv(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
    private sealed record VolumeDescriptor(string Namespace,string SystemId,string VolumeId,string PublisherId,string DataPreparerId,string ApplicationId,uint RootExtent,uint RootLength);
    private sealed record FsRecord(string Path,uint Extent,uint Length,byte Flags,bool IsDirectory);
    private sealed record NamePair(string IsoPath,string JolietPath,uint Extent,uint Length,byte Flags);
    private sealed record PendingTail(string Path,uint Extent,uint Length,long FinalLba,int Offset,byte[] Bytes,int NonZeroBytes,List<long> Deltas);
    private sealed record EofObservation(string Path,uint Extent,uint Length,int TailOffset,int TailLength,string Status,int NonZeroBytes,int MatchCount,string DeltaSectors);
    private sealed record ImageEvidence(string Media,bool UdfPresent,VolumeDescriptor? Primary,VolumeDescriptor? Joliet,List<FsRecord> Iso,List<NamePair> Pairs,List<EofObservation> Eofs);

    private sealed class SectorReader
    {
        private readonly FileStream _fs; public int PhysicalSectorSize{get;} private int UserOffset{get;}
        private SectorReader(FileStream fs,int physical,int offset){_fs=fs;PhysicalSectorSize=physical;UserOffset=offset;}
        public static async Task<SectorReader> DetectAsync(FileStream fs,CancellationToken ct)
        {
            byte[] h = new byte[32];
            fs.Position = 0;
            int n = await fs.ReadAsync(h, ct).ConfigureAwait(false);
            fs.Position = 0;

            // Raw CD-ROM sectors begin with the 12-byte sync pattern
            // 00 FF FF FF FF FF FF FF FF FF FF 00. Bytes 12-14 are the
            // BCD MSF address and therefore must NOT be assumed to be zero.
            // Byte 15 is the sector mode (1 or 2).
            bool rawCdSync = n >= 16
                && h[0] == 0x00
                && h[11] == 0x00
                && h[15] is 0x01 or 0x02;
            if (rawCdSync)
            {
                for (int i = 1; i <= 10; i++)
                    if (h[i] != 0xFF) { rawCdSync = false; break; }
            }

            if (rawCdSync)
                return new(fs, 2352, h[15] == 0x02 ? 24 : 16);

            return new(fs, 2048, 0);
        }
        public async Task<byte[]> ReadAsync(long lba,CancellationToken ct){byte[] b=new byte[2048];_fs.Position=checked(lba*PhysicalSectorSize+UserOffset);int p=0;while(p<b.Length){int n=await _fs.ReadAsync(b.AsMemory(p),ct);if(n==0)throw new EndOfStreamException();p+=n;}return b;}
        public async Task<byte[]> ReadBytesAsync(uint extent,uint length,CancellationToken ct){byte[] all=new byte[length];int p=0;long l=extent;while(p<all.Length){byte[] s=await ReadAsync(l++,ct);int take=Math.Min(2048,all.Length-p);Buffer.BlockCopy(s,0,all,p,take);p+=take;}return all;}
    }
}
