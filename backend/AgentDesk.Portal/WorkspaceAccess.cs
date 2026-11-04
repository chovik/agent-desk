using System.Collections.Concurrent;
using System.Text.Json;
namespace AgentDesk.Portal;

// Fixed stripes bound lock memory. Lifecycle transitions and file I/O share a lock;
// model execution itself does not hold one, so live reads work while Codex runs.
public sealed class RunLocks
{
    private readonly SemaphoreSlim[] _locks=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1)).ToArray();
    public async Task<IDisposable> Enter(string id,CancellationToken ct)
    {var gate=_locks[(uint)StringComparer.Ordinal.GetHashCode(id)%64];await gate.WaitAsync(ct);return new Held(gate);}
    private sealed class Held(SemaphoreSlim gate):IDisposable{public void Dispose()=>gate.Release();}
}
public sealed record WorkspaceEntry(string Name,string Kind,long Size);
public sealed record WorkspaceListing(string Path,IReadOnlyList<WorkspaceEntry> Entries,int Skipped,bool Truncated);
public sealed class WorkspaceAccess(RunStore store,IExecutionPlatform platform,ArtifactStore artifacts,RunLocks locks,PortalOptions options)
{
    public static bool SafePath(string path,bool root=false)=>(root&&path=="")||(ArtifactStore.SafePath(path)&&path.Split('/').Length<=32);
    private async Task<T> Access<T>(string owner,string id,Func<Run,CancellationToken,Task<T>> action,CancellationToken ct)
    {
        store.GetOwned(owner,id);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var held=await locks.Enter(id,timeout.Token);
        var run=store.GetOwned(owner,id);
        if(run.PlatformPhase!="Running"||run.State is not ("running" or "browsing"))throw new PortalException(409,"Workspace is not open. Open it explicitly; this does not start a Codex turn.");
        store.Update(id,r=>r.State=="browsing"?r with{BrowseExpiresAt=DateTimeOffset.UtcNow.AddSeconds(options.BrowseIdleSeconds)}:r);
        try{return await action(run,timeout.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new PortalException(504,"Workspace request timed out. Refresh the run state before retrying.");}
    }
    public Task<WorkspaceListing> List(string owner,string id,string path,CancellationToken ct)
    {
        store.GetOwned(owner,id);if(!SafePath(path,true))throw new PortalException(400,"Invalid workspace path.");
        return Access(owner,id,(r,t)=>platform.ListWorkspaceAsync(r,path,t),ct);
    }
    public Task<byte[]> Read(string owner,string id,string path,CancellationToken ct)
    {
        store.GetOwned(owner,id);if(!SafePath(path))throw new PortalException(400,"Invalid workspace path.");
        return Access(owner,id,(r,t)=>platform.ReadWorkspaceAsync(r,path,t),ct);
    }
    public Task<FileSnapshot?> Export(string owner,string id,CancellationToken ct)=>Access(owner,id,async(r,t)=>
    {
        var capture=await platform.CaptureAsync(r,t);artifacts.Save(r,capture.Files,capture.Skipped);
        store.Append(id,"system","An explicit saved export was captured from the live workspace. Ordinary browsing does not store file contents.");
        return artifacts.List(r);
    },ct);
}
public sealed partial class AxExecutionPlatform
{
    private async Task<(WorkspaceListing? Listing,byte[] Data)> Workspace(Run run,string operation,string path,CancellationToken ct)
    {
        using var source=typeof(AxExecutionPlatform).Assembly.GetManifestResourceStream("workspace-files")!;using var reader=new StreamReader(source);var program=await reader.ReadToEndAsync(ct);
        WorkspaceListing? listing=null;using var data=new MemoryStream();bool complete=false;
        var exit=await Frames(run,["python3","-u","-c",program],new{operation,path},15,null,(frame,_)=>
        {
            if(complete)throw new IOException("Unexpected workspace frame.");
            switch(frame.GetProperty("type").GetString())
            {
                case "workspace-error":
                    var status=frame.GetProperty("status").GetInt32();
                    throw new PortalException(status is 400 or 403 or 404 or 409 or 413?status:502,status==413?"Live file exceeds the 8 MiB read limit.":status==409?"File changed while reading; retry.":"Workspace path is unavailable or blocked.");
                case "directory":
                    if(operation!="list"||frame.GetProperty("path").GetString()!=path)throw new IOException("Invalid directory response.");
                    var entries=frame.GetProperty("entries").Deserialize<List<WorkspaceEntry>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    if(entries.Count>500||entries.Any(e=>!SafeEntry(e)))throw new IOException("Invalid directory entries.");
                    listing=new(path,entries,frame.GetProperty("skipped").GetInt32(),frame.GetProperty("truncated").GetBoolean());complete=true;break;
                case "data":
                    if(operation!="read")throw new IOException("Unexpected data.");
                    var bytes=Convert.FromBase64String(frame.GetProperty("data").GetString()!);
                    if(data.Length+bytes.Length>8*1024*1024)throw new IOException("Live file limit exceeded.");data.Write(bytes);break;
                case "file":
                    if(operation!="read"||frame.GetProperty("size").GetInt64()!=data.Length)throw new IOException("Incomplete file.");complete=true;break;
                default:throw new IOException("Unexpected workspace response.");
            }
            return Task.CompletedTask;
        },ct);
        if(exit!=0||!complete)throw new IOException("Workspace helper did not complete.");return(listing,data.ToArray());
    }
    private static bool SafeEntry(WorkspaceEntry e)=>WorkspaceAccess.SafePath(e.Name)&&!e.Name.Contains('/')&&e.Kind is "directory" or "file" or "blocked"&&e.Size>=0;
    public async Task<WorkspaceListing> ListWorkspaceAsync(Run run,string path,CancellationToken ct)=>(await Workspace(run,"list",path,ct)).Listing!;
    public async Task<byte[]> ReadWorkspaceAsync(Run run,string path,CancellationToken ct)=>(await Workspace(run,"read",path,ct)).Data;
}
