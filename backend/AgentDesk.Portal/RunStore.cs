using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace AgentDesk.Portal;

public sealed class RunStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public RunStore(PortalOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDirectory, "portal.db"), Pooling = false }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, owner TEXT NOT NULL, data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS run_owner ON runs(owner);
            CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL, created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS event_run ON events(run_id,id);
            CREATE TABLE IF NOT EXISTS jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, kind TEXT NOT NULL, message TEXT NOT NULL, state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS requests (owner TEXT NOT NULL, key TEXT NOT NULL, fingerprint TEXT NOT NULL, run_id TEXT NOT NULL, PRIMARY KEY(owner,key));
            """;
        cmd.ExecuteNonQuery();
    }
    // Explicit SQLite transaction SQL keeps every command on the same connection
    // without accidentally issuing a command outside its transaction.
    private sealed class Transaction(SqliteConnection connection) : IDisposable
    {
        private bool _committed;
        private readonly int _started = Execute(connection,"BEGIN IMMEDIATE");
        private static int Execute(SqliteConnection c,string sql) { using var cmd=c.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteNonQuery(); }
        public void Commit() { Execute(connection,"COMMIT");_committed=true; }
        public void Dispose() { if(!_committed)Execute(connection,"ROLLBACK"); }
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static SqliteCommand Command(SqliteConnection c, string sql, params (string, object?)[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static Run? Get(SqliteConnection c, string id)
    {
        using var cmd = Command(c, "SELECT data FROM runs WHERE id=$id", ("$id",id));
        return cmd.ExecuteScalar() is string data ? JsonSerializer.Deserialize<Run>(data,Json) : null;
    }
    private static void Save(SqliteConnection c, Run run)
    {
        using var cmd = Command(c, "INSERT INTO runs(id,owner,data) VALUES($id,$owner,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data",
            ("$id",run.Id),("$owner",run.Owner),("$data",JsonSerializer.Serialize(run,Json))); cmd.ExecuteNonQuery();
    }
    private static void Event(SqliteConnection c, string id, string kind, string text)
    {
        using var cmd = Command(c, "INSERT INTO events(run_id,kind,text,created) VALUES($id,$kind,$text,$now)",
            ("$id",id),("$kind",kind),("$text",text.Length > 16000 ? text[..16000]+" [truncated]" : text),("$now",Now())); cmd.ExecuteNonQuery();
    }
    public Run GetOwned(string owner, string id)
    {
        var run = Get(id);
        return run?.Owner == owner ? run : throw new PortalException(404,"Run not found.");
    }
    public Run? Get(string id) { lock (_gate) { using var c=Open(); return Get(c,id); } }
    public IReadOnlyList<Run> List(string owner)
    {
        lock (_gate)
        {
            using var c=Open(); using var cmd=Command(c,"SELECT data FROM runs WHERE owner=$owner ORDER BY rowid DESC LIMIT 100",("$owner",owner));
            using var r=cmd.ExecuteReader(); var list=new List<Run>(); while(r.Read()) list.Add(JsonSerializer.Deserialize<Run>(r.GetString(0),Json)!); return list;
        }
    }
    private static Run? Replayed(SqliteConnection c, string owner, string key, string fingerprint)
    {
        if (!Guid.TryParse(key,out _)) throw new PortalException(400,"A UUID Idempotency-Key header is required.");
        using var cmd=Command(c,"SELECT fingerprint,run_id FROM requests WHERE owner=$owner AND key=$key",("$owner",owner),("$key",key));
        using var r=cmd.ExecuteReader(); if (!r.Read()) return null;
        if(r.GetString(0)!=fingerprint) throw new PortalException(409,"Idempotency key was used for a different request.");
        var id=r.GetString(1);r.Close();return Get(c,id);
    }
    private static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json))));
    private static void Request(SqliteConnection c,string owner,string key,string hash,string id,string kind,string message)
    {
        using var req=Command(c,"INSERT INTO requests VALUES($owner,$key,$hash,$id)",("$owner",owner),("$key",key),("$hash",hash),("$id",id));req.ExecuteNonQuery();
        using var job=Command(c,"INSERT INTO jobs(run_id,kind,message,state) VALUES($id,$kind,$message,'queued')",("$id",id),("$kind",kind),("$message",message));job.ExecuteNonQuery();
    }
    public Run Create(string owner,CreateRunRequest input,string key,string? connectionId=null,GithubBinding? repository=null)
    {
        Catalog.Validate(input);
        if((input.Repository is null)!=(repository is null))throw new PortalException(400,"Repository must be resolved by the backend.");
        lock(_gate)
        {
            using var c=Open();using var tx=new Transaction(c);var hash=Fingerprint(input);
            if(Replayed(c,owner,key,hash) is {} replay) return replay;
            using var pending=Command(c,"SELECT COUNT(*) FROM jobs WHERE state IN ('queued','active')");
            if(Convert.ToInt64(pending.ExecuteScalar())>=20) throw new PortalException(429,"Lab queue is full.");
            if(input.Template=="codex-task"&&string.IsNullOrEmpty(connectionId))throw new PortalException(409,"Connect your personal ChatGPT account first.");
            var run=new Run(Guid.NewGuid().ToString("N"),owner,input.Title.Trim(),input.Instructions,input.Template,"queued","NotCreated",0,null,Now(),Now(),null){ConnectionId=connectionId,Repository=repository};
            Save(c,run);Event(c,run.Id,"user",input.Instructions);Event(c,run.Id,"system",input.Template=="codex-task"?"Queued for a real Codex turn in AX, bound to your current personal connection.":"Queued for a real AX environment check. No AI model or repository access is used.");
            Request(c,owner,key,hash,run.Id,"start",input.Instructions);tx.Commit();return run;
        }
    }
    public Run QueueAction(string owner,string id,string action,string message,string key)
    {
        if(message is null || message.Length>8000) throw new PortalException(400,"Message is too long.");
        lock(_gate)
        {
            using var c=Open();using var tx=new Transaction(c);var run=Get(c,id);
            if(run?.Owner!=owner) throw new PortalException(404,"Run not found.");
            var hash=Fingerprint(new {id,action,message});
            if(Replayed(c,owner,key,hash) is {} replay)return replay;
            if(run.Terminal) throw new PortalException(409,"This run has finished.");
            bool allowed=action switch
            {
                "browse" => run.State is "review" or "suspended" or "failed",
                "close-workspace" => run.State is "browsing" or "opening-workspace",
                "resume" => run.State is "review" or "suspended" or "failed",
                "finish" => run.State is "review" or "suspended" or "failed",
                "suspend" => run.State is "running" or "preparing" or "resuming" or "browsing" or "opening-workspace" or "review" or "recovery-required",
                "cancel" => run.State is not ("cancelling" or "finishing" or "publishing"),
                "publish" => run.State is "review" or "suspended" && run.Repository is not null && run.Publication is {Head:not null} p && p.CheckedTurn==run.Turn && p.Head==message && p.State is "ready" or "failed",
                _ => false
            };
            if(!allowed) throw new PortalException(409,"Action is not available in the current state.");
            if(action=="browse")
            {
                using var slots=Command(c,"SELECT COUNT(*) FROM jobs WHERE state IN ('queued','active')");
                if(Convert.ToInt64(slots.ExecuteScalar())>0)throw new PortalException(409,"The lab worker is busy. Try opening the workspace after its current operation finishes.");
            }
            if(action is "cancel" or "suspend" or "close-workspace")
            { using var drop=Command(c,"UPDATE jobs SET state='superseded' WHERE run_id=$id AND state='queued'",("$id",id));drop.ExecuteNonQuery(); }
            var returnState=run.State;
            var next=action switch {"publish"=>"publishing","browse"=>"opening-workspace","close-workspace"=>"pausing","resume"=>"queued","suspend"=>"pausing","cancel"=>"cancelling",_=>"finishing"};
            run=run with {State=next,UpdatedAt=Now(),Error=action=="browse"?run.Error:null,BrowseExpiresAt=null};Save(c,run);
            if(action=="resume"&&run.Publication is not null){run=run with{Publication=run.Publication with{State="not-ready",Head=null,Diff="",Checks="",Error=null}};Save(c,run);}
            if(action=="publish"){run=run with{Publication=run.Publication! with{State="publishing",Error=null}};Save(c,run);}
            if(action!="publish"&&!string.IsNullOrWhiteSpace(message)) Event(c,id,"user",message);
            Event(c,id,"system",$"{action} requested; platform confirmation pending.");
            Request(c,owner,key,hash,id,action,action=="browse"?returnState:message);tx.Commit();return run;
        }
    }
    // Final state and optional publication job commit together; resume cannot race auto-publish.
    public void FinishTurn(string id,int exit,string phase,GitPublication? publication)
    {
        lock(_gate){using var c=Open();using var tx=new Transaction(c);var r=Get(c,id)!;
            if(r.State is "cancelling" or "pausing")return;
            var automatic=exit==0&&r.Repository?.PublishMode=="auto"&&publication is {State:"ready",Head:not null}&&publication.CheckedTurn==r.Turn;
            r=r with{State=automatic?"publishing":exit==0?"review":"failed",PlatformPhase=phase,Publication=automatic?publication! with{State="publishing"}:publication,Error=exit==0?null:$"Sandbox command exited with code {exit}.",UpdatedAt=Now()};Save(c,r);
            Event(c,id,"system","AX confirmed suspension. Workspace retained; active sandbox resources released.");
            if(automatic){using var job=Command(c,"INSERT INTO jobs(run_id,kind,message,state) VALUES($id,'publish',$head,'queued')",("$id",id),("$head",publication!.Head));job.ExecuteNonQuery();Event(c,id,"system","Opt-in automatic publication queued: successful turn, committed clean tree and required local check passed.");}
            tx.Commit();
        }
    }
    public void Update(string id,Func<Run,Run> update,string? eventText=null)
    {
        lock(_gate){using var c=Open();using var tx=new Transaction(c);var run=Get(c,id) ?? throw new InvalidOperationException("Missing run");Save(c,update(run) with {UpdatedAt=Now()});if(eventText is not null)Event(c,id,"system",eventText);tx.Commit();}
    }
    public void Append(string id,string kind,string text){lock(_gate){using var c=Open();Event(c,id,kind,text);}}
    public IReadOnlyList<RunEvent> Events(string owner,string id,long after)
    {
        lock(_gate)
        {
            using var c=Open();if(Get(c,id)?.Owner!=owner)throw new PortalException(404,"Run not found.");
            using var cmd=Command(c,"SELECT id,kind,text,created FROM events WHERE run_id=$id AND id>$after ORDER BY id LIMIT 200",("$id",id),("$after",after));
            using var r=cmd.ExecuteReader();var list=new List<RunEvent>();while(r.Read())list.Add(new(r.GetInt64(0),id,r.GetString(1),r.GetString(2),r.GetString(3)));return list;
        }
    }
    public bool HasWaitingWork()
    {lock(_gate){using var c=Open();using var cmd=Command(c,"SELECT COUNT(*) FROM jobs WHERE state='queued'");return Convert.ToInt64(cmd.ExecuteScalar())>0;}}
    public WorkItem? Claim()
    {
        lock(_gate)
        {
            using var c=Open();using var tx=new Transaction(c);using var cmd=Command(c,"SELECT id,run_id,kind,message FROM jobs WHERE state='queued' AND (kind IN ('cancel','suspend','close-workspace','finish') OR NOT EXISTS (SELECT 1 FROM runs WHERE json_extract(data,'$.state')='recovery-required')) ORDER BY CASE WHEN kind IN ('cancel','suspend','close-workspace','finish') THEN 0 ELSE 1 END,id LIMIT 1");
            using var r=cmd.ExecuteReader();if(!r.Read())return null;var job=new WorkItem(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3));r.Close();
            using var claim=Command(c,"UPDATE jobs SET state='active' WHERE id=$id",("$id",job.Id));claim.ExecuteNonQuery();tx.Commit();return job;
        }
    }
    public void Complete(long id){lock(_gate){using var c=Open();using var cmd=Command(c,"UPDATE jobs SET state='done' WHERE id=$id",("$id",id));cmd.ExecuteNonQuery();}}
    public IReadOnlyList<WorkItem> Interrupted()
    {
        lock(_gate){using var c=Open();using var cmd=Command(c,"SELECT id,run_id,kind,message FROM jobs WHERE state='active'");using var r=cmd.ExecuteReader();var list=new List<WorkItem>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3)));return list;}
    }
}
