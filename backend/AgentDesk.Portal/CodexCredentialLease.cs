using System.Text.Json;
namespace AgentDesk.Portal;
public sealed partial class CodexConnections
{
    private readonly Dictionary<string,CancellationTokenSource> _leases=new();
    public string CurrentConnection(string owner,string? expected=null)
    {
        var saved=vault.Get(owner);
        if(saved is null||saved.RequiresReconnect||expected is not null&&saved.ConnectionId!=expected)throw new PortalException(409,"Connect/reconnect the original ChatGPT account for this run. Accounts are never switched silently.");
        return saved.ConnectionId;
    }
    public sealed class CredentialLease(string account,string token,CancellationToken stop,Func<Task> release) : IAsyncDisposable
    {
        public string AccountId=>account;public string AccessToken=>token;public CancellationToken Stop=>stop;
        public ValueTask DisposeAsync()=>new(release());
    }
    public async Task<CredentialLease> Acquire(string owner,string? connectionId,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            if(connectionId is null)throw new PortalException(409,"No bound account.");
            CurrentConnection(owner,connectionId);
            if(_pending.ContainsKey(owner)||_leases.ContainsKey(owner))throw new PortalException(409,"An account sign-in or turn is already active.");
            var saved=vault.Get(owner)!;
            using var auth=JsonDocument.Parse(saved.AuthJson);var token=auth.RootElement.GetProperty("tokens").GetProperty("access_token").GetString()!;
            var part=token.Split('.')[1].Replace('-','+').Replace('_','/');part=part.PadRight((part.Length+3)/4*4,'=');
            using var claims=JsonDocument.Parse(Convert.FromBase64String(part));
            if(claims.RootElement.GetProperty("exp").GetInt64()<DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds())
            {
                var home=Path.Combine(options.CodexScratch,"refresh-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(home,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(home,"auth.json"),saved.AuthJson,ct);
                    await using var rpc=new CodexRpc(options,home);await rpc.Initialize(ct);await rpc.Call("account/read",new{refreshToken=true},ct);
                    var refreshed=await File.ReadAllTextAsync(Path.Combine(home,"auth.json"),ct);
                    if(CodexVault.AccountId(refreshed)!=saved.AccountId)throw new IOException("Account changed");
                    saved=saved with{AuthJson=refreshed,UpdatedAt=DateTimeOffset.UtcNow.ToString("O")};vault.Save(saved);
                    using var value=JsonDocument.Parse(refreshed);token=value.RootElement.GetProperty("tokens").GetProperty("access_token").GetString()!;
                }
                catch{vault.Save(saved with{RequiresReconnect=true});throw new PortalException(409,"Account renewal failed; reconnect before retrying. No refresh replay was attempted.");}
                finally{Directory.Delete(home,true);}
            }
            var stop=new CancellationTokenSource();_leases[owner]=stop;
            return new(saved.AccountId,token,stop.Token,async()=>{await _gate.WaitAsync();try{_leases.Remove(owner);stop.Dispose();}finally{_gate.Release();}});
        }finally{_gate.Release();}
    }
}
