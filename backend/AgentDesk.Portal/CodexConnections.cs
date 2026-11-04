using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
namespace AgentDesk.Portal;

public sealed record CodexConnectionView(bool Enabled,string State,string? ConnectionId=null,string? Email=null,string? Plan=null,string? VerificationUrl=null,string? UserCode=null,string? ExpiresAt=null,string? Error=null);
public sealed partial class CodexConnections(PortalOptions options,CodexVault vault) : IAsyncDisposable
{
    private sealed class Pending(CodexRpc rpc,string home,string loginId,string url,string code)
    {
        public CodexRpc Rpc=rpc;public string Home=home,LoginId=loginId,Url=url,Code=code;
        public DateTimeOffset Expires=DateTimeOffset.UtcNow.AddMinutes(10);
        public CancellationTokenSource Stop=new(TimeSpan.FromMinutes(10));
    }
    private readonly SemaphoreSlim _gate=new(1);
    private readonly ConcurrentDictionary<string,Pending> _pending=new();
    private readonly ConcurrentDictionary<string,string> _errors=new();
    public CodexConnectionView View(string owner)
    {
        var saved=vault.Get(owner);
        if(_pending.TryGetValue(owner,out var p))return new(vault.Enabled,"pending",saved?.ConnectionId,saved?.Email,saved?.Plan,p.Url,p.Code,p.Expires.ToString("O"));
        return new(vault.Enabled,saved is null?"disconnected":saved.RequiresReconnect?"reconnect-required":"connected",saved?.ConnectionId,saved?.Email,saved?.Plan,Error:saved?.RequiresReconnect==true?"Reconnect this account before running Codex.":_errors.GetValueOrDefault(owner));
    }
    public async Task<CodexConnectionView> Start(string owner,CancellationToken ct)
    {
        if(!vault.Enabled)throw new PortalException(503,"Codex sign-in is not enabled.");
        await _gate.WaitAsync(ct);try
        {
            if(_leases.ContainsKey(owner))throw new PortalException(409,"Wait for your active Codex turn to finish, or disconnect it first.");
            if(_pending.ContainsKey(owner))return View(owner);
            if(_pending.Count>=2)throw new PortalException(429,"Two sign-ins are already pending. Try again later.");
            var home=Path.Combine(options.CodexScratch,"login-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(home,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            CodexRpc? rpc=null;
            try
            {
                rpc=new(options,home);await rpc.Initialize(ct);
                var result=await rpc.Call("account/login/start",new{type="chatgptDeviceCode"},ct);
                var url=result.GetProperty("verificationUrl").GetString()!;
                if(url!="https://auth.openai.com/codex/device")throw new PortalException(502,"Unexpected device sign-in destination; sign-in was stopped.");
                var p=new Pending(rpc,home,result.GetProperty("loginId").GetString()!,url,result.GetProperty("userCode").GetString()!);
                _pending[owner]=p;_errors.TryRemove(owner,out _);_ = Complete(owner,p);return View(owner);
            }
            catch{if(rpc is not null)await rpc.DisposeAsync();Directory.Delete(home,true);throw;}
        }finally{_gate.Release();}
    }
    private async Task Complete(string owner,Pending p)
    {
        try
        {
            await foreach(var message in p.Rpc.Notifications.Reader.ReadAllAsync(p.Stop.Token))
            {
                if(message.GetProperty("method").GetString()!="account/login/completed")continue;
                var value=message.GetProperty("params");if(value.GetProperty("loginId").GetString()!=p.LoginId)continue;
                if(!value.GetProperty("success").GetBoolean())throw new IOException("Managed sign-in failed");
                var account=(await p.Rpc.Call("account/read",new{refreshToken=false},p.Stop.Token)).GetProperty("account");
                if(account.GetProperty("type").GetString()!="chatgpt")throw new IOException("Unexpected auth type");
                var auth=await File.ReadAllTextAsync(Path.Combine(p.Home,"auth.json"),p.Stop.Token);var id=CodexVault.AccountId(auth);
                await _gate.WaitAsync(p.Stop.Token);try
                {
                    if(!_pending.TryGetValue(owner,out var current)||current!=p||p.Stop.IsCancellationRequested)return;
                    var previous=vault.Get(owner);var connectionId=previous?.AccountId==id?previous.ConnectionId:Guid.NewGuid().ToString("N");
                    vault.Save(new(connectionId,owner,id,account.GetProperty("email").GetString(),account.GetProperty("planType").GetString(),auth,DateTimeOffset.UtcNow.ToString("O")));
                    _pending.TryRemove(owner,out _);_errors.TryRemove(owner,out _);
                }finally{_gate.Release();}
                return;
            }
            throw new IOException("Sign-in process ended");
        }
        catch(Exception)
        {
            await _gate.WaitAsync();try{if(_pending.TryGetValue(owner,out var current)&&current==p){_pending.TryRemove(owner,out _);_errors[owner]="Sign-in expired or failed. Enable device-code login in ChatGPT security settings and retry. No new connection was saved.";}}finally{_gate.Release();}
        }
        finally{await p.Rpc.DisposeAsync();try{Directory.Delete(p.Home,true);}catch(IOException){}p.Stop.Dispose();}
    }
    public async Task<CodexConnectionView> Cancel(string owner,bool disconnect,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            if(_pending.TryRemove(owner,out var p))p.Stop.Cancel();
            if(disconnect){if(_leases.TryGetValue(owner,out var active))active.Cancel();vault.Delete(owner);}_errors.TryRemove(owner,out _);return View(owner);
        }finally{_gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var p in _pending.Values){p.Stop.Cancel();await p.Rpc.DisposeAsync();}
    }
}
