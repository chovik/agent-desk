using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GuestRpc=Ateenv.V1Alpha;
namespace AgentDesk.Portal;
public sealed partial class AxExecutionPlatform
{
    private async Task<int> Frames(Run run,string[] command,object? initial,int seconds,Action<string>? started,
        Func<JsonElement,Func<object,Task>,Task> handle,CancellationToken ct)
    {
        using var connection=Guest();var client=new GuestRpc.ProcessService.ProcessServiceClient(connection.Channel);
        var headers=new Metadata{{"ate-target-actor",options.Atespace+"/"+run.AxName}};
        var request=new GuestRpc.StartProcessRequest{Cwd="/workspace",Stdin=initial is not null,Timeout=Duration.FromTimeSpan(TimeSpan.FromSeconds(seconds))};request.Command.AddRange(command);
        var process=await client.StartProcessAsync(request,headers,deadline:DateTime.UtcNow.AddSeconds(20),cancellationToken:ct);started?.Invoke(process.ProcessId);
        using var writes=client.WriteProcessInput(headers,deadline:DateTime.UtcNow.AddSeconds(seconds+20),cancellationToken:ct);
        using var writeGate=new SemaphoreSlim(1);
        async Task Send(object value)
        {
            await writeGate.WaitAsync(ct);try{await writes.RequestStream.WriteAsync(new(){ProcessId=process.ProcessId,Data=ByteString.CopyFromUtf8(JsonSerializer.Serialize(value)+"\n")},ct);}finally{writeGate.Release();}
        }
        try
        {
            if(initial is not null)await Send(initial);
            using var stream=client.StreamProcessOutput(new(){ProcessId=process.ProcessId,Follow=true},headers,deadline:DateTime.UtcNow.AddSeconds(seconds+15),cancellationToken:ct);
            var pending=new StringBuilder();long total=0;
            await foreach(var chunk in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(chunk.OutputCase==GuestRpc.ProcessOutput.OutputOneofCase.Exit)
                {if(pending.Length!=0)throw new IOException("Incomplete guest frame.");return chunk.Exit.ExitCode;}
                // Driver stderr is deliberately not a browser/provider diagnostic channel.
                if(chunk.OutputCase!=GuestRpc.ProcessOutput.OutputOneofCase.Stdout)continue;
                total+=chunk.Stdout.Length;if(total>32L*1024*1024)throw new IOException("Guest transport limit exceeded.");
                foreach(var ch in chunk.Stdout.ToStringUtf8())
                {
                    if(ch=='\n'){if(pending.Length>0){using var doc=JsonDocument.Parse(pending.ToString());pending.Clear();await handle(doc.RootElement.Clone(),Send);}}
                    else {pending.Append(ch);if(pending.Length>3*1024*1024)throw new IOException("Guest frame limit exceeded.");}
                }
            }
            throw new IOException("Guest stream ended without process exit.");
        }
        finally
        {
            try{await client.SignalProcessAsync(new(){ProcessId=process.ProcessId,Signal=GuestRpc.Signal.Term},headers,deadline:DateTime.UtcNow.AddSeconds(5));}catch(RpcException){}
        }
    }
    public async Task<(IReadOnlyList<SavedFile> Files,int Skipped)> CaptureAsync(Run run,CancellationToken ct)
    {
        using var source=typeof(AxExecutionPlatform).Assembly.GetManifestResourceStream("workspace-export")!;using var reader=new StreamReader(source);var program=await reader.ReadToEndAsync(ct);
        var files=new List<SavedFile>();var skipped=0;var summary=false;long total=0;
        var exit=await Frames(run,["python3","-u","-c",program],null,30,null,(frame,_)=>
        {
            if(frame.GetProperty("type").GetString()=="file")
            {
                if(summary||files.Count>=256)throw new IOException("Invalid export.");
                var path=frame.GetProperty("path").GetString()!;var data=Convert.FromBase64String(frame.GetProperty("data").GetString()!);total+=data.Length;
                if(!ArtifactStore.SafePath(path)||data.Length>1024*1024||total>8*1024*1024)throw new IOException("Export limits exceeded.");files.Add(new(path,data));
            }
            else if(frame.GetProperty("type").GetString()=="summary")
            {if(summary||frame.GetProperty("files").GetInt32()!=files.Count||frame.GetProperty("bytes").GetInt64()!=total)throw new IOException("Invalid export summary.");summary=true;skipped=frame.GetProperty("skipped").GetInt32();}
            else throw new IOException("Unexpected export frame.");
            return Task.CompletedTask;
        },ct);
        if(exit!=0||!summary)throw new IOException("Export did not complete.");return(files,skipped);
    }
    private async Task<int> ExecuteCodex(Run run,string message,Action<string> started,Action<string,string> output,CancellationToken ct)
    {
        await using var credential=await connections.Acquire(run.Owner,run.ConnectionId,ct);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,credential.Stop);stop.CancelAfter(TimeSpan.FromMinutes(5));
        using var bridge=new ModelBridge(options,credential,output);var requests=new List<Task>();bool completed=false;int shown=0;bool limited=false;
        try
        {
            var exit=await Frames(run,["python3","-u","/opt/agent-desk/codex_driver.py"],new{message,threadId=run.ThreadId,model=options.CodexModel,cwd=run.Repository is null?"/workspace":"/workspace/repo",repositoryInstructions=run.Repository is null?"":$"Work on branch {run.PublishBranch}. Make focused commits, keep the worktree clean, and run this required check: {run.Repository.CheckCommand}. Do not push, create PRs, amend earlier published commits or change .github/workflows. The portal handles publication outside this sandbox."},300,started,(frame,send)=>
            {
                var type=frame.GetProperty("type").GetString();
                if(type=="model-request")
                {
                    if(requests.Count>=8||requests.Any(t=>!t.IsCompleted))throw new IOException("Model request/concurrency limit reached.");
                    requests.Add(bridge.Forward(frame,send,stop.Token));
                }
                else if(type=="thread")
                {
                    var id=frame.GetProperty("id").GetString();if(!Guid.TryParse(id,out _) || run.ThreadId is not null&&id!=run.ThreadId)throw new IOException("Unexpected agent session ID.");
                    store.Update(run.Id,r=>r with{ThreadId=id});
                }
                else if(type=="result")completed=frame.GetProperty("status").GetString()=="completed";
                else if(type is "agent" or "command" or "command-output" or "command-result" or "changes" or "error")
                {
                    var text=frame.GetProperty("text").GetString()??"";shown+=text.Length;
                    if(shown<=512*1024)output(type,text.Length>16000?text[..16000]:text);
                    else if(!limited){output("system","Turn display reached its 512 KiB limit; further agent output is omitted.");limited=true;}
                }
                else throw new IOException("Unexpected guest event.");
                return Task.CompletedTask;
            },stop.Token);
            await Task.WhenAll(requests);return exit==0&&completed?0:1;
        }
        finally{await stop.CancelAsync();try{await Task.WhenAll(requests);}catch(Exception){}}
    }
}
