using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDesk.Portal;
using Xunit;
namespace AgentDesk.Portal.Tests;
public sealed class CodexConnectionTests
{
    private const string FakeAuth="""{"OPENAI_API_KEY":null,"tokens":{"account_id":"test-account","access_token":"fake-access-secret","refresh_token":"fake-refresh-secret","id_token":"fake-id"}}""";
    private static string Fixture(PortalFactory f)
    {
        var path=Path.Combine(f.Root,"fake-codex");
        File.WriteAllText(path,"#!/usr/bin/python3\n"+"""
import os,sys,json,time,threading
home=os.environ['CODEX_HOME']
root=os.path.dirname(os.path.dirname(home))
lock=threading.Lock()
def emit(v):
 with lock: print(json.dumps(v),flush=True)
def done():
 while not os.path.exists(root+'/complete-login'):time.sleep(.03)
 with open(home+'/auth.json','w') as f:f.write(AUTH)
 emit({'method':'account/login/completed','params':{'loginId':'test-login','success':True,'error':None}})
for line in sys.stdin:
 x=json.loads(line);method=x.get('method');i=x.get('id')
 if method=='initialize':emit({'id':i,'result':{'userAgent':'fixture'}})
 elif method=='account/login/start':
  emit({'id':i,'result':{'type':'chatgptDeviceCode','loginId':'test-login','verificationUrl':'https://auth.openai.com/codex/device','userCode':'TEST-ONLY'}})
  threading.Thread(target=done,daemon=True).start()
 elif method=='account/read':emit({'id':i,'result':{'account':{'type':'chatgpt','email':'fixture@example.test','planType':'plus'},'requiresOpenaiAuth':True}})
""".Replace("AUTH",JsonSerializer.Serialize(FakeAuth)));
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);return path;
    }
    [Fact] public void VaultEncryptsAndRejectsWrongKey()
    {
        var root=Path.Combine(Path.GetTempPath(),"codex-vault-test-"+Guid.NewGuid());Directory.CreateDirectory(root);var key=Path.Combine(root,"key");File.WriteAllBytes(key,RandomNumberGenerator.GetBytes(32));
        var options=new PortalOptions{DataDirectory=root,CodexEnabled=true,CodexVaultKey=key};var vault=new CodexVault(options);
        vault.Save(new("connection","alice","test-account","a@example.test","plus",FakeAuth,"now"));Assert.Equal(FakeAuth,vault.Get("alice")!.AuthJson);Assert.Null(vault.Get("bob"));
        Assert.DoesNotContain("fake-access-secret",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"codex-vault.db"))));
        File.WriteAllBytes(key,RandomNumberGenerator.GetBytes(32));var wrong=new CodexVault(options);Assert.ThrowsAny<CryptographicException>(()=>wrong.Get("alice"));
        vault.Delete("alice");Assert.Null(vault.Get("alice"));
    }
    [Fact] public async Task DeviceLoginIsOwnerBoundEncryptedAndNeverReturnsTokens()
    {
        await using var f=new PortalFactory{EnableCodex=true};f.CodexBinary=Fixture(f);using var a=await f.Login();using var b=await f.Login("bob");
        var start=await a.PostAsJsonAsync("/api/connections/codex/login",new{});start.EnsureSuccessStatusCode();var text=await start.Content.ReadAsStringAsync();Assert.Contains("TEST-ONLY",text);Assert.DoesNotContain("fake-access-secret",text);
        var other=await b.GetStringAsync("/api/connections/codex");Assert.DoesNotContain("TEST-ONLY",other);Assert.Contains("disconnected",other);
        // Browser-supplied owner is not accepted/used by any connection endpoint.
        var duplicate=await a.PostAsJsonAsync("/api/connections/codex/login",new{});Assert.Equal("pending",(await duplicate.Content.ReadFromJsonAsync<CodexConnectionView>())!.State);
        File.WriteAllText(Path.Combine(f.Root,"complete-login"),"go");CodexConnectionView? connected=null;
        for(var i=0;i<100;i++){connected=await a.GetFromJsonAsync<CodexConnectionView>("/api/connections/codex");if(connected?.State=="connected")break;await Task.Delay(50);}
        Assert.Equal("connected",connected!.State);Assert.Equal("fixture@example.test",connected.Email);
        text=await a.GetStringAsync("/api/connections/codex");Assert.DoesNotContain("fake-access-secret",text);Assert.DoesNotContain("fake-refresh-secret",text);Assert.DoesNotContain("TEST-ONLY",text);
        var vault=new CodexVault(new(){DataDirectory=f.Root,CodexEnabled=true,CodexVaultKey=Path.Combine(f.Root,"vault.key")});Assert.Equal("test-account",vault.Get("alice")!.AccountId);Assert.Null(vault.Get("bob"));
        (await a.PostAsJsonAsync("/api/connections/codex/disconnect",new{})).EnsureSuccessStatusCode();Assert.Null(vault.Get("alice"));
    }
    [Fact] public async Task CancelledLoginCannotLaterSaveCredentials()
    {
        await using var f=new PortalFactory{EnableCodex=true};f.CodexBinary=Fixture(f);using var c=await f.Login();
        (await c.PostAsJsonAsync("/api/connections/codex/login",new{})).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/connections/codex/cancel",new{})).EnsureSuccessStatusCode();File.WriteAllText(Path.Combine(f.Root,"complete-login"),"late");await Task.Delay(200);
        Assert.Equal("disconnected",(await c.GetFromJsonAsync<CodexConnectionView>("/api/connections/codex"))!.State);
    }
    [Fact] public async Task ConnectionWritesRequireAuthenticationAndCsrf()
    {
        await using var f=new PortalFactory();using var c=f.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/connections/codex")).StatusCode);
        using var signed=await f.Login();signed.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");Assert.Equal(HttpStatusCode.BadRequest,(await signed.PostAsJsonAsync("/api/connections/codex/login",new{})).StatusCode);
    }
}
