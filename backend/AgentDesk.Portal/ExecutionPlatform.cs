using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using AxRpc = Ax.V1Alpha1;
using GuestRpc = Ateenv.V1Alpha;
namespace AgentDesk.Portal;

public interface IExecutionPlatform
{
    Task InstallRepositoryAsync(Run run,byte[] bundle,CancellationToken ct)=>throw new NotSupportedException();
    Task<RepositoryCapture> CaptureRepositoryAsync(Run run,CancellationToken ct)=>throw new NotSupportedException();
    Task<WorkspaceListing> ListWorkspaceAsync(Run run,string path,CancellationToken ct)=>throw new NotSupportedException();
    Task<byte[]> ReadWorkspaceAsync(Run run,string path,CancellationToken ct)=>throw new NotSupportedException();
    Task EnsureAsync(Run run,CancellationToken ct);
    Task<string> ResumeAsync(Run run,CancellationToken ct);
    Task<string> SuspendAsync(Run run,CancellationToken ct);
    Task DeleteAsync(Run run,CancellationToken ct);
    Task<int> ExecuteAsync(Run run,string message,Action<string> started,Action<string,string> output,CancellationToken ct);
    Task<(IReadOnlyList<SavedFile> Files,int Skipped)> CaptureAsync(Run run,CancellationToken ct)=>Task.FromResult<(IReadOnlyList<SavedFile>,int)>((Array.Empty<SavedFile>(),0));
}

public sealed partial class AxExecutionPlatform(PortalOptions options,CodexConnections connections,RunStore store) : IExecutionPlatform, IDisposable
{
    private readonly GrpcChannel _ax=GrpcChannel.ForAddress(options.AxAddress);
    private AxRpc.AX.AXClient Client => new(_ax);
    private static DateTime Deadline => DateTime.UtcNow.AddSeconds(90);
    public void Dispose()=>_ax.Dispose();
    public async Task EnsureAsync(Run run,CancellationToken ct)
    {
        var workspace=new AxRpc.Workspace { ApiVersion="ax.io/v1alpha1",Kind="Workspace",Metadata=new(){Name=run.AxName,Atespace=options.Atespace},Spec=new() };
        workspace.Spec.Files.Add(new AxRpc.File{Path="portal-seed.txt",Content="Agent Desk authenticated sandbox"});
        await Client.UpdateWorkspaceAsync(new(){Workspace=workspace},deadline:Deadline,cancellationToken:ct);
        try { await Client.GetTaskAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);return; }
        catch(RpcException e)when(e.StatusCode==StatusCode.NotFound) { }
        var task=new AxRpc.Task { ApiVersion="ax.io/v1alpha1",Kind="Task",Metadata=new(){Name=run.AxName,Atespace=options.Atespace},Spec=new(){
            Image=run.Template=="codex-task"?options.CodexRunnerImage:options.RunnerImage,Debug=true,Workspace=new(){Name=run.AxName,Path="/workspace"},Resources=new(){Limits=new(){Cpu="1",Memory=run.Template=="codex-task"?"1Gi":"512Mi"}}}};
        task.Spec.Command.AddRange(["sleep","infinity"]);
        try { await Client.CreateTaskAsync(new(){Task=task},deadline:Deadline,cancellationToken:ct); }
        catch(RpcException e)when(e.StatusCode==StatusCode.AlreadyExists) { }
    }
    public async Task<string> ResumeAsync(Run run,CancellationToken ct)
    {
        var task=await Client.ResumeTaskAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);
        if(task.Status?.Phase!="Running" || !task.Status.Conditions.Any(c=>c.Type=="Ready" && c.Status=="True"))throw new InvalidOperationException("AX did not confirm a ready Running task.");
        return task.Status.Phase;
    }
    public async Task<string> SuspendAsync(Run run,CancellationToken ct)
    {
        try
        {
            var existing=await Client.GetTaskAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);
            var task=existing.Status?.Phase=="Suspended" ? existing : await Client.SuspendTaskAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);
            if(task.Status?.Phase!="Suspended")throw new InvalidOperationException("AX did not confirm suspension.");
            return "Suspended";
        }
        catch(RpcException e)when(e.StatusCode==StatusCode.NotFound){return "NotCreated";}
    }
    public async Task DeleteAsync(Run run,CancellationToken ct)
    {
        try{await Client.DeleteTaskAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);}
        catch(RpcException e)when(e.StatusCode==StatusCode.NotFound){}
        try{await Client.DeleteWorkspaceAsync(new(){Atespace=options.Atespace,Name=run.AxName},deadline:Deadline,cancellationToken:ct);}
        catch(RpcException e)when(e.StatusCode==StatusCode.NotFound){}
    }
    private sealed class GuestLease(GrpcChannel channel,X509Certificate2 certificate,X509Certificate2Collection roots) : IDisposable
    {
        public GrpcChannel Channel => channel;
        public void Dispose(){channel.Dispose();certificate.Dispose();foreach(var root in roots)root.Dispose();}
    }
    private GuestLease Guest()
    {
        // Read fresh projected credentials for every turn, rather than retaining expired certs.
        var certificate=X509Certificate2.CreateFromPemFile(options.IdentityBundle,options.IdentityBundle);
        var roots=new X509Certificate2Collection();roots.ImportFromPemFile(options.CaBundle);
        var handler=new SocketsHttpHandler { SslOptions=new(){ClientCertificates=new(){certificate},
            RemoteCertificateValidationCallback=(_,cert,chain,errors)=>
            {
                if(cert is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch)!=0 || (errors & SslPolicyErrors.RemoteCertificateNotAvailable)!=0)return false;
                using var custom=new X509Chain();custom.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
                custom.ChainPolicy.CustomTrustStore.AddRange(roots);custom.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                custom.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                if(chain is not null)foreach(var entry in chain.ChainElements)custom.ChainPolicy.ExtraStore.Add(entry.Certificate);
                using var remote=new X509Certificate2(cert);return custom.Build(remote);
            }}};
        return new(GrpcChannel.ForAddress(options.RouterAddress,new(){HttpHandler=handler,DisposeHttpClient=true}),certificate,roots);
    }
    public async Task<int> ExecuteAsync(Run run,string message,Action<string> started,Action<string,string> output,CancellationToken ct)
    {
        if(run.Template=="codex-task")return await ExecuteCodex(run,message,started,output,ct);
        using var lease=Guest();var client=new GuestRpc.ProcessService.ProcessServiceClient(lease.Channel);
        var headers=new Metadata{{"ate-target-actor",options.Atespace+"/"+run.AxName}};
        var payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {runId=run.Id,turn=run.Turn,message,fail=run.Template=="failure-check"})));
        var request=new GuestRpc.StartProcessRequest { Cwd="/workspace",Timeout=Duration.FromTimeSpan(TimeSpan.FromSeconds(45)) };
        request.Command.AddRange(["python3","-u","-c",CheckProgram,payload]);
        var process=await client.StartProcessAsync(request,headers,deadline:DateTime.UtcNow.AddSeconds(15),cancellationToken:ct);
        started(process.ProcessId);var total=0;bool reportedLimit=false;
        try
        {
            using var stream=client.StreamProcessOutput(new(){ProcessId=process.ProcessId,Follow=true},headers,deadline:DateTime.UtcNow.AddSeconds(60),cancellationToken:ct);
            await foreach(var item in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(item.OutputCase==GuestRpc.ProcessOutput.OutputOneofCase.Exit)return item.Exit.ExitCode;
                var bytes=item.OutputCase==GuestRpc.ProcessOutput.OutputOneofCase.Stderr?item.Stderr:item.Stdout;
                total+=bytes.Length;
                if(total<=256*1024)output(item.OutputCase==GuestRpc.ProcessOutput.OutputOneofCase.Stderr?"stderr":"stdout",bytes.ToStringUtf8());
                else if(!reportedLimit){output("system","Output exceeded the 256 KiB turn limit; further output is omitted.");reportedLimit=true;}
            }
            throw new IOException("Guest output ended without a confirmed process exit.");
        }
        catch
        {
            try { await client.SignalProcessAsync(new(){ProcessId=process.ProcessId,Signal=GuestRpc.Signal.Term},headers,deadline:DateTime.UtcNow.AddSeconds(5)); }catch(RpcException){}
            throw;
        }
    }
    private const string CheckProgram="""
import base64,json,os,sys,time,hashlib
p=json.loads(base64.b64decode(sys.argv[1]))
state_file='/workspace/portal-session.json'
previous=json.load(open(state_file)) if os.path.exists(state_file) else {'turns':[]}
print('Real sandbox command started; this is an environment check, not an AI response.',flush=True)
print('Persisted earlier turns:',len(previous['turns']),flush=True)
print('Seed:',open('/workspace/portal-seed.txt').read().strip(),flush=True)
previous['turns'].append({'turn':p['turn'],'message':p['message']})
with open(state_file+'.tmp','w') as f:
 json.dump(previous,f);f.flush();os.fsync(f.fileno())
os.replace(state_file+'.tmp',state_file)
print('Saved turn',p['turn'],'in /workspace/portal-session.json',flush=True)
time.sleep(1)
print('Session SHA256:',hashlib.sha256(open(state_file,'rb').read()).hexdigest(),flush=True)
if p['fail']:
 print('Intentional failing check: returning exit code 7.',file=sys.stderr,flush=True)
 sys.exit(7)
print('Environment check passed. Feedback will be recorded by a new real command after resume.',flush=True)
""";
}
