using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace AgentDesk.Portal;

// App private keys never cross into the browser/guest after GitHub's manifest exchange.
public sealed record GithubCredential(string Owner,string ConnectionId,long AppId,string Slug,string Pem,long? InstallationId=null,string? Account=null,bool AllowAllRepositories=false);
public sealed record GithubView(bool Enabled,string State,string? ConnectionId,string? Account,string? InstallUrl,bool AllRepositoriesAllowed=false);
public sealed record GithubInstallation(long Id,string Account,string RepositorySelection="selected");
public sealed record GithubInstallationRequest(long Id,bool AllowAllRepositories=false);
public sealed record GithubRepository(long Id,string FullName,string DefaultBranch);
public sealed record GithubBinding(string ConnectionId,long RepositoryId,string FullName,string BaseBranch,string BaseSha,string CheckCommand,string PublishMode);
public sealed record GithubRepoRequest(long RepositoryId,string BaseBranch,string CheckCommand,string PublishMode="manual");
public sealed class GithubVault
{
    private readonly string _db;private readonly byte[]? _key;private readonly object _gate=new();
    public bool Enabled=>_key is not null;
    public GithubVault(PortalOptions options)
    {
        _db=new SqliteConnectionStringBuilder{DataSource=Path.Combine(options.DataDirectory,"github-vault.db"),Pooling=false}.ToString();
        if(!options.GithubEnabled)return;
        _key=File.ReadAllBytes(options.CodexVaultKey);if(_key.Length!=32)throw new InvalidOperationException("GitHub vault requires the retained external 32-byte key.");
        using var c=Open();using var q=c.CreateCommand();q.CommandText="CREATE TABLE IF NOT EXISTS apps(owner TEXT PRIMARY KEY,cipher BLOB NOT NULL); CREATE TABLE IF NOT EXISTS setups(hash TEXT PRIMARY KEY,owner TEXT UNIQUE NOT NULL,expires INTEGER NOT NULL)";q.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var c=new SqliteConnection(_db);c.Open();return c;}
    private byte[] Protect(string owner,byte[] plain){var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[plain.Length];using var aes=new AesGcm(_key!,16);aes.Encrypt(nonce,plain,cipher,tag,Encoding.UTF8.GetBytes("github:"+owner));return [..nonce,..tag,..cipher];}
    public GithubCredential? Get(string owner)
    {
        if(!Enabled)return null;lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT cipher FROM apps WHERE owner=$o";q.Parameters.AddWithValue("$o",owner);if(q.ExecuteScalar() is not byte[] blob)return null;
        var plain=new byte[blob.Length-28];using var aes=new AesGcm(_key!,16);aes.Decrypt(blob.AsSpan(0,12),blob.AsSpan(28),blob.AsSpan(12,16),plain,Encoding.UTF8.GetBytes("github:"+owner));try{return JsonSerializer.Deserialize<GithubCredential>(plain);}finally{CryptographicOperations.ZeroMemory(plain);}}
    }
    public void Save(GithubCredential value){if(!Enabled)throw new PortalException(503,"GitHub integration is disabled.");var plain=JsonSerializer.SerializeToUtf8Bytes(value);byte[] cipher;try{cipher=Protect(value.Owner,plain);}finally{CryptographicOperations.ZeroMemory(plain);}lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="INSERT INTO apps VALUES($o,$c) ON CONFLICT(owner) DO UPDATE SET cipher=excluded.cipher";q.Parameters.AddWithValue("$o",value.Owner);q.Parameters.AddWithValue("$c",cipher);q.ExecuteNonQuery();}}
    public void Delete(string owner){if(!Enabled)return;lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM apps WHERE owner=$o; DELETE FROM setups WHERE owner=$o";q.Parameters.AddWithValue("$o",owner);q.ExecuteNonQuery();}}
    public string Setup(string owner)
    {
        if(!Enabled)throw new PortalException(503,"GitHub integration is disabled.");var state=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM setups WHERE owner=$o OR expires<$now; INSERT INTO setups VALUES($h,$o,$until)";q.Parameters.AddWithValue("$o",owner);q.Parameters.AddWithValue("$h",Hash(state));q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());q.Parameters.AddWithValue("$until",DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());q.ExecuteNonQuery();}return state;
    }
    public string TakeSetup(string state)
    {
        if(!Enabled||!Regex.IsMatch(state??"","^[a-f0-9]{64}$"))throw new PortalException(400,"Invalid or expired GitHub setup.");
        lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM setups WHERE hash=$h AND expires>=$now RETURNING owner";q.Parameters.AddWithValue("$h",Hash(state!));q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());return q.ExecuteScalar() as string??throw new PortalException(400,"Invalid or expired GitHub setup.");}
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
public interface IGithubApi { Task<JsonElement> Send(string method,string path,string? token,object? body,CancellationToken ct); }
public sealed class GithubApi : IGithubApi,IDisposable
{
    private readonly HttpClient _http;
    public GithubApi(PortalOptions options){var handler=new HttpClientHandler{AllowAutoRedirect=false};if(!string.IsNullOrEmpty(options.CodexProxy)){handler.UseProxy=true;handler.Proxy=new WebProxy(options.CodexProxy);}else handler.UseProxy=false;_http=new(handler){Timeout=TimeSpan.FromSeconds(45)};}
    public async Task<JsonElement> Send(string method,string path,string? token,object? body,CancellationToken ct)
    {
        if(!path.StartsWith('/')||path.StartsWith("//")||path.Contains("..")||path.Contains('\\'))throw new IOException("Invalid GitHub route.");
        using var req=new HttpRequestMessage(new HttpMethod(method),"https://api.github.com"+path);
        req.Headers.UserAgent.ParseAdd("AgentDesk-Demo/0.3");req.Headers.Accept.Add(new("application/vnd.github+json"));req.Headers.Add("X-GitHub-Api-Version","2022-11-28");
        if(token is not null)req.Headers.Authorization=new("Bearer",token);if(body is not null)req.Content=JsonContent.Create(body);
        using var res=await SendRequest(req,ct);
        if(!res.IsSuccessStatusCode)throw new PortalException((int)res.StatusCode==404?404:502,$"GitHub rejected the operation (HTTP {(int)res.StatusCode}). Check the App installation and repository permissions.");
        if(res.StatusCode==HttpStatusCode.NoContent)return JsonSerializer.SerializeToElement(new{});
        await using var input=await res.Content.ReadAsStreamAsync(ct);using var data=new MemoryStream();var buffer=new byte[16384];int n;
        while((n=await input.ReadAsync(buffer,ct))>0){if(data.Length+n>4*1024*1024)throw new IOException("GitHub response limit exceeded.");data.Write(buffer,0,n);}
        return JsonSerializer.Deserialize<JsonElement>(data.ToArray());
    }
    private async Task<HttpResponseMessage> SendRequest(HttpRequestMessage request,CancellationToken ct)
    {
        try{return await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);}
        catch(HttpRequestException){throw new PortalException(502,"Cannot reach GitHub through the approved backend network path. No operation was confirmed.");}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new PortalException(502,"GitHub request timed out. Check its status before retrying.");}
    }
    public void Dispose()=>_http.Dispose();
}
public sealed class GithubConnections(PortalOptions options,GithubVault vault,IGithubApi api)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> _gates=new();
    private SemaphoreSlim Gate(string owner)=>_gates.GetOrAdd(owner,_=>new(1));
    public GithubView View(string owner){var c=vault.Get(owner);return new(vault.Enabled,c is null?"disconnected":c.InstallationId is null?"install-required":"connected",c?.ConnectionId,c?.Account,c is null?null:$"https://github.com/apps/{c.Slug}/installations/new",c?.AllowAllRepositories??false);}
    public object Start(string owner)
    {
        if(!Uri.TryCreate(options.PublicOrigin,UriKind.Absolute,out var origin)||origin.Scheme!="https")throw new PortalException(409,"GitHub setup requires public HTTPS.");
        if(vault.Get(owner) is not null)throw new PortalException(409,"A GitHub App is already registered. Install it or disconnect first.");
        var state=vault.Setup(owner);
        return new {url="https://github.com/settings/apps/new?state="+state,manifest=new{name="Agent Desk "+state[..8],url=options.PublicOrigin,redirect_url=options.PublicOrigin+"/github/setup/callback",setup_url=options.PublicOrigin+"/#/connections",@public=false,hook_attributes=new{url=options.PublicOrigin+"/github/events",active=false},default_events=Array.Empty<string>(),default_permissions=new{contents="write",pull_requests="write"}}};
    }
    public async Task Complete(string state,string code,CancellationToken ct)
    {
        if(!Regex.IsMatch(code??"","^[a-zA-Z0-9_-]{10,200}$"))throw new PortalException(400,"Invalid GitHub setup code.");var owner=vault.TakeSetup(state);var gate=Gate(owner);await gate.WaitAsync(ct);try{
        if(vault.Get(owner) is not null)throw new PortalException(409,"GitHub connection changed during setup.");
        var result=await api.Send("POST","/app-manifests/"+code+"/conversions",null,new{},ct);var slug=result.GetProperty("slug").GetString()??"";var pem=result.GetProperty("pem").GetString()??"";var id=result.GetProperty("id").GetInt64();
        if(id<=0||!Regex.IsMatch(slug,"^[a-zA-Z0-9-]{1,100}$")||pem.Length>12000)throw new IOException("Invalid GitHub App response.");using var rsa=RSA.Create();rsa.ImportFromPem(pem);if(rsa.KeySize<2048)throw new IOException("GitHub App key is too small.");
        vault.Save(new(owner,Guid.NewGuid().ToString("N"),id,slug,pem));}finally{gate.Release();}
    }
    public static string Jwt(GithubCredential c)
    {
        static string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
        var now=DateTimeOffset.UtcNow;var text=B64(JsonSerializer.SerializeToUtf8Bytes(new{alg="RS256",typ="JWT"}))+"."+B64(JsonSerializer.SerializeToUtf8Bytes(new{iat=now.AddSeconds(-60).ToUnixTimeSeconds(),exp=now.AddMinutes(8).ToUnixTimeSeconds(),iss=c.AppId.ToString()}));
        using var rsa=RSA.Create();rsa.ImportFromPem(c.Pem);return text+"."+B64(rsa.SignData(Encoding.ASCII.GetBytes(text),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
    }
    private GithubCredential Current(string owner,string? connection=null){var c=vault.Get(owner)??throw new PortalException(409,"Connect GitHub first.");if(connection is not null&&c.ConnectionId!=connection)throw new PortalException(409,"This run belongs to a different GitHub connection.");return c;}
    public async Task<GithubInstallation[]> Installations(string owner,CancellationToken ct)
    {
        var c=Current(owner);var result=await api.Send("GET","/app/installations?per_page=100",Jwt(c),null,ct);
        return result.EnumerateArray().Where(x=>x.GetProperty("repository_selection").GetString() is "selected" or "all" &&(!x.TryGetProperty("suspended_at",out var suspended)||suspended.ValueKind==JsonValueKind.Null)).Select(x=>new GithubInstallation(x.GetProperty("id").GetInt64(),x.GetProperty("account").GetProperty("login").GetString()!,x.GetProperty("repository_selection").GetString()!)).ToArray();
    }
    public async Task<GithubView> Select(string owner,long id,CancellationToken ct,bool allowAllRepositories=false)
    {
        var gate=Gate(owner);if(!await gate.WaitAsync(0,ct))throw new PortalException(409,"GitHub is in use; try again after the operation.");try{
        var c=Current(owner);if(c.InstallationId is not null&&c.InstallationId!=id)throw new PortalException(409,"Disconnect before changing the GitHub installation.");
        var installation=(await Installations(owner,ct)).SingleOrDefault(i=>i.Id==id)??throw new PortalException(400,"Install this App on GitHub, then refresh installations.");
        if(installation.RepositorySelection=="all"&&!allowAllRepositories)throw new PortalException(400,"All repositories includes current and future repositories. Explicitly confirm this wider access, or select individual repositories on GitHub.");
        vault.Save(c with{InstallationId=id,Account=installation.Account,AllowAllRepositories=installation.RepositorySelection=="all"&&allowAllRepositories});return View(owner);}finally{gate.Release();}
    }
    public async Task Disconnect(string owner,CancellationToken ct){var gate=Gate(owner);if(!await gate.WaitAsync(0,ct))throw new PortalException(409,"GitHub is in use; wait until publishing/checkout finishes.");try{vault.Delete(owner);}finally{gate.Release();}}
    public sealed class Lease(string token,GithubCredential connection,Action release) : IDisposable {public string Token=>token;public GithubCredential Connection=>connection;public void Dispose()=>release();}
    public async Task<Lease> Acquire(string owner,string? connection,long? repositoryId,bool write,CancellationToken ct)
    {
        var gate=Gate(owner);await gate.WaitAsync(ct);try{
        var c=Current(owner,connection);if(c.InstallationId is null)throw new PortalException(409,"Install and select your GitHub App first.");
        // Recheck selection policy every lease, not just when connecting.
        var installation=await api.Send("GET","/app/installations/"+c.InstallationId,Jwt(c),null,ct);
        var selection=installation.GetProperty("repository_selection").GetString();
        if((selection!="selected"&&(selection!="all"||!c.AllowAllRepositories))||installation.TryGetProperty("suspended_at",out var suspended)&&suspended.ValueKind!=JsonValueKind.Null)throw new PortalException(409,"GitHub App must be active. All-repository access must be explicitly confirmed in Connections.");
        var body=new Dictionary<string,object>{{"permissions",write?new Dictionary<string,string>{{"contents","write"},{"pull_requests","write"}}:new Dictionary<string,string>{{"contents","read"}}}};if(repositoryId is not null)body["repository_ids"]=new[]{repositoryId.Value};
        var result=await api.Send("POST",$"/app/installations/{c.InstallationId}/access_tokens",Jwt(c),body,ct);var token=result.GetProperty("token").GetString();if(string.IsNullOrWhiteSpace(token)||token.Length>16000)throw new IOException("Invalid installation token.");
        return new(token,c,()=>gate.Release());}catch{gate.Release();throw;}
    }
    public async Task<GithubRepository[]> Repositories(string owner,CancellationToken ct)
    {
        using var lease=await Acquire(owner,null,null,false,ct);var result=new List<GithubRepository>();
        for(var page=1;page<=5;page++){var data=await api.Send("GET",$"/installation/repositories?per_page=100&page={page}",lease.Token,null,ct);var rows=data.GetProperty("repositories").EnumerateArray().ToArray();foreach(var r in rows){if(r.GetProperty("archived").GetBoolean()||r.GetProperty("disabled").GetBoolean())continue;result.Add(new(r.GetProperty("id").GetInt64(),r.GetProperty("full_name").GetString()!,r.GetProperty("default_branch").GetString()!));}if(rows.Length<100)break;}return result.ToArray();
    }
    public static bool SafeBranch(string s)=>s is not null&&Regex.IsMatch(s,"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")&&!s.Contains("..")&&!s.Contains("//")&&!s.EndsWith('/')&&!s.EndsWith('.')&&!s.Split('/').Any(p=>p.StartsWith('.')||p.EndsWith(".lock"));
    public static bool SafeRepo(string s)=>s is not null&&Regex.IsMatch(s,"^[a-zA-Z0-9][a-zA-Z0-9-]{0,99}/[a-zA-Z0-9_.-]{1,100}$")&&!s.EndsWith("/.")&&!s.EndsWith("/..");
    public async Task<GithubBinding> Bind(string owner,GithubRepoRequest request,CancellationToken ct)
    {
        if(request.RepositoryId<=0||!SafeBranch(request.BaseBranch)||string.IsNullOrWhiteSpace(request.CheckCommand)||request.CheckCommand.Length>1000||request.PublishMode is not ("manual" or "auto"))throw new PortalException(400,"Choose a repository, valid base branch, required check command and publishing mode.");
        using var lease=await Acquire(owner,null,request.RepositoryId,false,ct);var r=await api.Send("GET","/repositories/"+request.RepositoryId,lease.Token,null,ct);var name=r.GetProperty("full_name").GetString()!;if(!SafeRepo(name)||r.GetProperty("archived").GetBoolean()||r.GetProperty("disabled").GetBoolean())throw new PortalException(400,"Repository is unavailable.");
        var commit=await api.Send("GET",$"/repos/{name}/git/ref/heads/{Uri.EscapeDataString(request.BaseBranch)}",lease.Token,null,ct);var sha=commit.GetProperty("object").GetProperty("sha").GetString()!;if(!Regex.IsMatch(sha,"^[a-f0-9]{40}$"))throw new IOException("Unexpected Git commit.");return new(lease.Connection.ConnectionId,request.RepositoryId,name,request.BaseBranch,sha,request.CheckCommand,request.PublishMode);
    }
}
