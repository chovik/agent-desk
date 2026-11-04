using System.Security.Cryptography;
using System.Text.Json;
using AgentDesk.Portal;
using Xunit;
namespace AgentDesk.Portal.Tests;

public sealed class FakeGithubApi : IGithubApi
{
    public List<(string Method,string Path,string? Token,object? Body)> Calls {get;}=[];
    public Func<string,string,string?,object?,JsonElement> Reply {get;set;}=(_,_,_,_)=>JsonSerializer.SerializeToElement(new{});
    public Task<JsonElement> Send(string method,string path,string? token,object? body,CancellationToken ct){Calls.Add((method,path,token,body));return Task.FromResult(Reply(method,path,token,body));}
}
public sealed class GithubTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"agent-desk-github-"+Guid.NewGuid());
    private readonly PortalOptions _options;private readonly GithubVault _vault;private readonly FakeGithubApi _api=new();private readonly GithubConnections _connections;
    public GithubTests(){Directory.CreateDirectory(_root);File.WriteAllBytes(Path.Combine(_root,"key"),RandomNumberGenerator.GetBytes(32));_options=new(){DataDirectory=_root,CodexVaultKey=Path.Combine(_root,"key"),GithubEnabled=true,PublicOrigin="https://portal.example.test"};_vault=new(_options);_connections=new(_options,_vault,_api);}
    public void Dispose()=>Directory.Delete(_root,true);
    private GithubCredential Credential(string owner="alice",long? installation=42){using var rsa=RSA.Create(2048);return new(owner,"original-connection",12,"agent-desk-test",rsa.ExportPkcs8PrivateKeyPem(),installation,"example-owner");}
    [Fact] public void SetupStatesAreOwnerBoundExpiringSingleUseAndReplaced()
    {var old=_vault.Setup("alice");var current=_vault.Setup("alice");Assert.Throws<PortalException>(()=>_vault.TakeSetup(old));Assert.Equal("alice",_vault.TakeSetup(current));Assert.Throws<PortalException>(()=>_vault.TakeSetup(current));Assert.Throws<PortalException>(()=>_vault.TakeSetup("anything"));}
    [Fact] public void KeysAreEncryptedAndNeverReturnedInConnectionViews()
    {var c=Credential();_vault.Save(c);Assert.Equal(c,_vault.Get("alice"));Assert.Null(_vault.Get("bob"));var raw=File.ReadAllText(Path.Combine(_root,"github-vault.db"));Assert.DoesNotContain("PRIVATE KEY",raw);var view=JsonSerializer.Serialize(_connections.View("alice"));Assert.DoesNotContain("PRIVATE KEY",view);Assert.DoesNotContain("Pem",view);Assert.Contains("connected",view);}
    [Fact] public void ManifestUsesLeastRequiredPermissionsAndNoWebhookSubscriptions()
    {var data=JsonSerializer.SerializeToElement(_connections.Start("alice"));var manifest=data.GetProperty("manifest");Assert.False(manifest.GetProperty("public").GetBoolean());Assert.False(manifest.GetProperty("hook_attributes").GetProperty("active").GetBoolean());Assert.Equal("write",manifest.GetProperty("default_permissions").GetProperty("contents").GetString());Assert.False(manifest.GetProperty("default_permissions").TryGetProperty("administration",out _));Assert.Empty(manifest.GetProperty("default_events").EnumerateArray());}
    [Fact] public async Task CallbackConsumesStateAndStoresOnlyTheBoundOwnersApp()
    {var state=_vault.Setup("alice");var c=Credential(installation:null);_api.Reply=(_,_,_,_)=>JsonSerializer.SerializeToElement(new{id=c.AppId,slug=c.Slug,pem=c.Pem,client_secret="not-retained"});await _connections.Complete(state,"temporary_code_12345",default);Assert.Equal(c.Pem,_vault.Get("alice")!.Pem);Assert.Null(_vault.Get("bob"));Assert.Equal("install-required",_connections.View("alice").State);await Assert.ThrowsAsync<PortalException>(()=>_connections.Complete(state,"temporary_code_12345",default));Assert.Single(_api.Calls);}
    [Fact] public async Task AllRepositoriesInstallationIsRejected()
    {_vault.Save(Credential());_api.Reply=(_,_,_,_)=>JsonSerializer.SerializeToElement(new{repository_selection="all",suspended_at=(string?)null});await Assert.ThrowsAsync<PortalException>(()=>_connections.Acquire("alice",null,123,true,default));Assert.Single(_api.Calls);}
    [Fact] public async Task LeaseScopesTokenAndBlocksDisconnectUntilReleased()
    {_vault.Save(Credential());_api.Reply=(_,path,_,_)=>path.EndsWith("access_tokens")?JsonSerializer.SerializeToElement(new{token="ephemeral-test-token"}):JsonSerializer.SerializeToElement(new{repository_selection="selected",suspended_at=(string?)null});using(var lease=await _connections.Acquire("alice","original-connection",123,true,default)){Assert.Equal("ephemeral-test-token",lease.Token);var body=JsonSerializer.SerializeToElement(_api.Calls.Last().Body);Assert.Equal(123,body.GetProperty("repository_ids")[0].GetInt64());Assert.Equal("write",body.GetProperty("permissions").GetProperty("pull_requests").GetString());await Assert.ThrowsAsync<PortalException>(()=>_connections.Disconnect("alice",default));}await _connections.Disconnect("alice",default);Assert.Null(_vault.Get("alice"));}
    [Fact] public async Task AllInstallationsAreVisibleButNeedExplicitConsent()
    {
        _vault.Save(Credential());_api.Reply=(_,_,_,_)=>JsonSerializer.SerializeToElement(new[]{new{id=42,account=new{login="example-owner"},repository_selection="all",suspended_at=(string?)null}});
        Assert.Equal("all",Assert.Single(await _connections.Installations("alice",default)).RepositorySelection);
        await Assert.ThrowsAsync<PortalException>(()=>_connections.Select("alice",42,default));Assert.False(_vault.Get("alice")!.AllowAllRepositories);
        var view=await _connections.Select("alice",42,default,true);Assert.True(view.AllRepositoriesAllowed);Assert.True(_vault.Get("alice")!.AllowAllRepositories);
    }
    [Fact] public async Task ConsentedAllRepositoryInstallationStillUsesSingleRepoToken()
    {
        _vault.Save(Credential() with{AllowAllRepositories=true});_api.Reply=(_,path,_,_)=>path.EndsWith("access_tokens")?JsonSerializer.SerializeToElement(new{token="ephemeral-test-token"}):JsonSerializer.SerializeToElement(new{repository_selection="all",suspended_at=(string?)null});
        using var lease=await _connections.Acquire("alice","original-connection",123,true,default);var body=JsonSerializer.SerializeToElement(_api.Calls.Last().Body);Assert.Equal(123,body.GetProperty("repository_ids")[0].GetInt64());Assert.Equal(1,body.GetProperty("repository_ids").GetArrayLength());
    }
    [Fact] public async Task AllConsentNeverBypassesSuspension()
    {
        _vault.Save(Credential() with{AllowAllRepositories=true});_api.Reply=(_,_,_,_)=>JsonSerializer.SerializeToElement(new{repository_selection="all",suspended_at="2026-11-03"});await Assert.ThrowsAsync<PortalException>(()=>_connections.Acquire("alice",null,123,true,default));Assert.Single(_api.Calls);
    }
    [Fact] public async Task SelectingNarrowerInstallationClearsAllAccessConsent()
    {
        _vault.Save(Credential() with{AllowAllRepositories=true});_api.Reply=(_,_,_,_)=>JsonSerializer.SerializeToElement(new[]{new{id=42,account=new{login="example-owner"},repository_selection="selected",suspended_at=(string?)null}});var view=await _connections.Select("alice",42,default,true);Assert.False(view.AllRepositoriesAllowed);
    }
    [Fact] public async Task ReconnectionNeverSilentlyRebindsExistingRuns()
    {_vault.Save(Credential());await Assert.ThrowsAsync<PortalException>(()=>_connections.Acquire("alice","different",123,true,default));Assert.Empty(_api.Calls);}
    [Theory][InlineData("main",true)][InlineData("feature/test",true)][InlineData("../main",false)][InlineData("--upload-pack=x",false)][InlineData("main.lock",false)][InlineData("refs//bad",false)]
    public void BranchValidationIsStrict(string value,bool expected)=>Assert.Equal(expected,GithubConnections.SafeBranch(value));
    [Fact] public void AppJwtIsShortLivedAndSigned()
    {var c=Credential();var pieces=GithubConnections.Jwt(c).Split('.');static byte[] Decode(string s)=>Convert.FromBase64String(s.Replace('-','+').Replace('_','/')+new string('=',(4-s.Length%4)%4));using var rsa=RSA.Create();rsa.ImportFromPem(c.Pem);Assert.True(rsa.VerifyData(System.Text.Encoding.ASCII.GetBytes(pieces[0]+"."+pieces[1]),Decode(pieces[2]),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));var payload=JsonSerializer.Deserialize<JsonElement>(Decode(pieces[1]));Assert.InRange(payload.GetProperty("exp").GetInt64()-payload.GetProperty("iat").GetInt64(),1,600);}
}
