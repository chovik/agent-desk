using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
namespace AgentDesk.Portal;

public sealed record SavedFile(string Path,byte[] Data);
public sealed record FileEntry(string Path,long Size,string Sha256);
public sealed record FileSnapshot(int Turn,string CapturedAt,int Skipped,IReadOnlyList<FileEntry> Files);
public sealed class ArtifactStore(PortalOptions options)
{
    private readonly object _gate=new();
    private SqliteConnection Open()
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(options.DataDirectory,"artifacts.db"),Pooling=false}.ToString());c.Open();
        using var q=c.CreateCommand();q.CommandText="CREATE TABLE IF NOT EXISTS snapshots(run TEXT PRIMARY KEY, owner TEXT, turn INTEGER, captured TEXT, skipped INTEGER); CREATE TABLE IF NOT EXISTS files(run TEXT,path TEXT,data BLOB,sha TEXT,PRIMARY KEY(run,path))";q.ExecuteNonQuery();return c;
    }
    public static bool SafePath(string path)=>path.Length is >0 and <=500 && !path.Contains('\\') && !path.Any(char.IsControl) && path.Split('/').All(p=>p.Length>0&&!p.StartsWith('.'));
    public void Save(Run run,IReadOnlyList<SavedFile> files,int skipped)
    {
        if(files.Count>256 || files.Any(f=>!SafePath(f.Path)||f.Data.Length>1024*1024) || files.Sum(f=>(long)f.Data.Length)>8*1024*1024 || files.Select(f=>f.Path).Distinct().Count()!=files.Count)throw new IOException("Invalid workspace export.");
        lock(_gate)
        {
            using var c=Open();using var tx=c.BeginTransaction();
            using var quota=c.CreateCommand();quota.Transaction=tx;quota.CommandText="SELECT COALESCE(SUM(length(f.data)),0), COALESCE(SUM(CASE WHEN s.owner=$owner THEN length(f.data) ELSE 0 END),0) FROM files f JOIN snapshots s ON s.run=f.run WHERE f.run<>$run";quota.Parameters.AddWithValue("$owner",run.Owner);quota.Parameters.AddWithValue("$run",run.Id);
            using(var r=quota.ExecuteReader()){r.Read();var size=files.Sum(f=>(long)f.Data.Length);if(r.GetInt64(0)+size>256L*1024*1024||r.GetInt64(1)+size>64L*1024*1024)throw new IOException("Saved-file quota reached.");}
            using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="DELETE FROM files WHERE run=$run; INSERT INTO snapshots VALUES($run,$owner,$turn,$time,$skipped) ON CONFLICT(run) DO UPDATE SET turn=excluded.turn,captured=excluded.captured,skipped=excluded.skipped";
            q.Parameters.AddWithValue("$run",run.Id);q.Parameters.AddWithValue("$owner",run.Owner);q.Parameters.AddWithValue("$turn",run.Turn);q.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));q.Parameters.AddWithValue("$skipped",Math.Clamp(skipped,0,100000));q.ExecuteNonQuery();
            foreach(var f in files){using var insert=c.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO files VALUES($run,$path,$data,$sha)";insert.Parameters.AddWithValue("$run",run.Id);insert.Parameters.AddWithValue("$path",f.Path);insert.Parameters.AddWithValue("$data",f.Data);insert.Parameters.AddWithValue("$sha",Convert.ToHexString(SHA256.HashData(f.Data)));insert.ExecuteNonQuery();}tx.Commit();
        }
    }
    public FileSnapshot? List(Run run)
    {
        lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT turn,captured,skipped FROM snapshots WHERE run=$id AND owner=$owner";q.Parameters.AddWithValue("$id",run.Id);q.Parameters.AddWithValue("$owner",run.Owner);using var r=q.ExecuteReader();if(!r.Read())return null;var turn=r.GetInt32(0);var time=r.GetString(1);var skipped=r.GetInt32(2);r.Close();q.CommandText="SELECT path,length(data),sha FROM files WHERE run=$id ORDER BY path";using var rows=q.ExecuteReader();var files=new List<FileEntry>();while(rows.Read())files.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2)));return new(turn,time,skipped,files);}
    }
    public byte[] Read(Run run,string path,int turn)
    {
        if(!SafePath(path))throw new PortalException(400,"Invalid file path.");
        lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT f.data FROM files f JOIN snapshots s ON s.run=f.run WHERE s.run=$id AND s.owner=$owner AND s.turn=$turn AND f.path=$path";q.Parameters.AddWithValue("$id",run.Id);q.Parameters.AddWithValue("$owner",run.Owner);q.Parameters.AddWithValue("$turn",turn);q.Parameters.AddWithValue("$path",path);return q.ExecuteScalar() as byte[]??throw new PortalException(404,"Saved file/version not found. Refresh the file list.");}
    }
}
