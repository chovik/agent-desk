using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
namespace AgentDesk.Portal;

// Private stdio only. Raw protocol/stderr may contain credentials: never log them.
public sealed class CodexRpc : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long,TaskCompletionSource<JsonElement>> _requests=new();
    private readonly SemaphoreSlim _write=new(1);
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _reader,_stderr;
    private long _next;private int _disposed;
    public Channel<JsonElement> Notifications {get;}=Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(128){FullMode=BoundedChannelFullMode.DropOldest});
    public CodexRpc(PortalOptions options,string home)
    {
        var start=new ProcessStartInfo(options.CodexBinary){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,WorkingDirectory=home};
        start.Environment.Clear();start.Environment["PATH"]="/usr/local/bin:/usr/bin:/bin";start.Environment["HOME"]=home;start.Environment["CODEX_HOME"]=home;start.Environment["TMPDIR"]="/tmp";
        if(!string.IsNullOrEmpty(options.CodexProxy)){start.Environment["HTTPS_PROXY"]=options.CodexProxy;start.Environment["HTTP_PROXY"]=options.CodexProxy;}
        foreach(var arg in new[]{"app-server","--listen","stdio://","-c","cli_auth_credentials_store=\"file\"","-c","forced_login_method=\"chatgpt\"","-c","analytics.enabled=false","-c","feedback.enabled=false"})start.ArgumentList.Add(arg);
        _process=Process.Start(start)??throw new PortalException(503,"Codex sign-in service could not start.");
        _reader=Read();_stderr=DrainErrors();
    }
    private async Task DrainErrors(){try{while(await _process.StandardError.ReadLineAsync(_stop.Token) is not null){}}catch(OperationCanceledException){} }
    private async Task Read()
    {
        try
        {
            while(await _process.StandardOutput.ReadLineAsync(_stop.Token) is {} line)
            {
                if(line.Length>1024*1024)throw new IOException("Oversized protocol message");
                using var doc=JsonDocument.Parse(line);var msg=doc.RootElement.Clone();
                if(msg.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.Number&&_requests.TryRemove(id.GetInt64(),out var tcs))
                {if(msg.TryGetProperty("error",out _))tcs.TrySetException(new PortalException(502,"Codex sign-in request failed. Check device-login permissions or try again."));else tcs.TrySetResult(msg.GetProperty("result").Clone());}
                else if(msg.TryGetProperty("method",out _))Notifications.Writer.TryWrite(msg);
            }
        }
        catch(Exception) when(!_stop.IsCancellationRequested){} // intentionally no raw diagnostic payload
        finally
        {
            foreach(var tcs in _requests.Values)tcs.TrySetException(new PortalException(503,"Codex sign-in process ended. Please retry."));
            Notifications.Writer.TryComplete();
        }
    }
    private async Task Send(object message,CancellationToken ct)
    {
        await _write.WaitAsync(ct);try{await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),ct);await _process.StandardInput.FlushAsync(ct);}finally{_write.Release();}
    }
    public async Task<JsonElement> Call(string method,object parameters,CancellationToken ct)
    {
        var id=Interlocked.Increment(ref _next);var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);_requests[id]=tcs;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try{await Send(new{id,method,@params=parameters},deadline.Token);return await tcs.Task.WaitAsync(deadline.Token);}finally{_requests.TryRemove(id,out _);}
    }
    public async Task Initialize(CancellationToken ct)
    {
        await Call("initialize",new{clientInfo=new{name="agent_desk_private_lab",title="Agent Desk private lab",version="0.1.0"},capabilities=new{experimentalApi=false}},ct);
        await Send(new{method="initialized"},ct);
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel();try{if(!_process.HasExited)_process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}
        try{await Task.WhenAll(_reader,_stderr).WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception){}
        _process.Dispose();_stop.Dispose();
    }
}
