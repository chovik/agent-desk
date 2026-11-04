using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentDesk.Portal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace AgentDesk.Portal.Tests;

public sealed class FakePlatform : IExecutionPlatform
{
    public int Executions,Reads,Captures,Deletes;
    public int? ExitCode;
    public TaskCompletionSource? Reading,ReleaseRead;
    public Task<WorkspaceListing> ListWorkspaceAsync(Run r,string path,CancellationToken ct)=>Task.FromResult(new WorkspaceListing(path,[new("a.txt","file",5)],0,false));
    public async Task<byte[]> ReadWorkspaceAsync(Run r,string path,CancellationToken ct){Interlocked.Increment(ref Reads);Reading?.TrySetResult();if(ReleaseRead is not null)await ReleaseRead.Task.WaitAsync(ct);return System.Text.Encoding.UTF8.GetBytes("hello");}
    public Task<(IReadOnlyList<SavedFile> Files,int Skipped)> CaptureAsync(Run r,CancellationToken ct){Interlocked.Increment(ref Captures);return Task.FromResult<(IReadOnlyList<SavedFile>,int)>(([new("a.txt",System.Text.Encoding.UTF8.GetBytes("hello"))],0));}
    public bool WaitForCancel;
    public bool RefuseSuspend;
    public bool RefuseDelete;
    public Task EnsureAsync(Run r,CancellationToken ct)=>Task.CompletedTask;
    public Task<string> ResumeAsync(Run r,CancellationToken ct)=>Task.FromResult("Running");
    public Task<string> SuspendAsync(Run r,CancellationToken ct)=>RefuseSuspend?Task.FromException<string>(new IOException("test release failure")):Task.FromResult("Suspended");
    public Task DeleteAsync(Run r,CancellationToken ct){if(RefuseDelete)return Task.FromException(new IOException("test deletion failure"));Interlocked.Increment(ref Deletes);return Task.CompletedTask;}
    public async Task<int> ExecuteAsync(Run r,string message,Action<string> started,Action<string,string> output,CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);started("process-"+r.Turn);output("stdout","Real-output transport fixture, turn "+r.Turn);
        if(WaitForCancel)await Task.Delay(Timeout.Infinite,ct);
        return ExitCode??(r.Template=="failure-check"?7:0);
    }
}
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    public string? PublicOrigin {get;set;}
    public string TrustedProxyNetworks {get;set;}="";
    public IPAddress? ProxyAddress {get;set;}
    public bool EnableCodex {get;set;}
    public int BrowseIdleSeconds {get;set;}=300;
    public string? CodexBinary {get;set;}
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"agent-desk-tests-"+Guid.NewGuid());
    public FakePlatform Platform {get;}=new();
    public IRepositoryWorkflow? Repositories {get;set;}
    public const string Password="integration-test-password-12345";
    public PortalFactory()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root,"vault.key"),System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var hash=new PasswordHasher<string>().HashPassword("unused",Password);
        File.WriteAllText(Path.Combine(Root,"users.json"),JsonSerializer.Serialize(new[]{new LabUser("alice","alice",hash),new LabUser("bob","bob",hash)}));
    }
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(c=>c.AddInMemoryCollection(new Dictionary<string,string?>{
            ["Portal:DataDirectory"]=Root,["Portal:UsersFile"]=Path.Combine(Root,"users.json"),
            ["Portal:RunnerImage"]="test@sha256:"+new string('a',64),["Portal:AllowInsecureLabCookie"]=PublicOrigin is null?"true":"false",
            ["Portal:PublicOrigin"]=PublicOrigin,["Portal:BrowseIdleSeconds"]=BrowseIdleSeconds.ToString(),
            ["Portal:TrustedProxyNetworks"]=TrustedProxyNetworks,
            ["Portal:CodexEnabled"]=EnableCodex.ToString(),["Portal:CodexVaultKey"]=Path.Combine(Root,"vault.key"),
            ["Portal:CodexScratch"]=Path.Combine(Root,"scratch"),["Portal:CodexBinary"]=CodexBinary??"/not-installed",
            ["Portal:StaticDirectory"]=Root}));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseContentRoot(AppContext.BaseDirectory);
        b.ConfigureServices(s=>{s.RemoveAll<IExecutionPlatform>();s.AddSingleton<IExecutionPlatform>(Platform);if(Repositories is not null){s.RemoveAll<IRepositoryWorkflow>();s.AddSingleton(Repositories);}if(ProxyAddress is not null)s.AddSingleton<IStartupFilter>(new TestProxyAddress(ProxyAddress));});
    }
    public async Task<HttpClient> Login(string username="alice")
    {
        var c=CreateClient();await RefreshCsrf(c);
        var response=await c.PostAsJsonAsync("/api/login",new{username,password=Password});response.EnsureSuccessStatusCode();
        await RefreshCsrf(c);return c;
    }
    public static async Task RefreshCsrf(HttpClient c)
    {
        var session=await c.GetFromJsonAsync<JsonElement>("/api/session");c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");c.DefaultRequestHeaders.Add("X-CSRF-TOKEN",session.GetProperty("csrfToken").GetString());
    }
    public static void Key(HttpClient c,string? key=null){c.DefaultRequestHeaders.Remove("Idempotency-Key");c.DefaultRequestHeaders.Add("Idempotency-Key",key??Guid.NewGuid().ToString());}
    public static async Task<Run> Create(HttpClient c,string template="environment-check")
    {
        Key(c);var response=await c.PostAsJsonAsync("/api/runs",new{title="Test run",instructions="Record this; never execute it as code",template});response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<Run>())!;
    }
    public static async Task<Run> Wait(HttpClient c,string id,params string[] states)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(true){var r=(await c.GetFromJsonAsync<Run>("/api/runs/"+id,timeout.Token))!;if(states.Contains(r.State))return r;await Task.Delay(50,timeout.Token);}
    }
}
public sealed class TestProxyAddress(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
    {
        app.Use((ctx,run)=>{ctx.Connection.RemoteIpAddress=address;return run(ctx);});next(app);
    };
}
public sealed class PortalTests
{
    [Theory]
    [InlineData("127.0.0.1",200)]
    [InlineData("::1",200)]
    [InlineData("203.0.113.10",400)]
    public async Task PublicHttpsForwardingOnlyTrustsPodLoopback(string address,int expected)
    {
        await using var f=new PortalFactory{PublicOrigin="https://portal.example.test",ProxyAddress=IPAddress.Parse(address)};
        using var c=f.CreateClient(new(){BaseAddress=new Uri("http://portal.example.test")});c.DefaultRequestHeaders.Add("X-Forwarded-Proto","https");
        var response=await c.GetAsync("/api/session");Assert.Equal(expected,(int)response.StatusCode);
        if(expected==200)Assert.All(response.Headers.GetValues("Set-Cookie"),cookie=>Assert.Contains("secure",cookie,StringComparison.OrdinalIgnoreCase));
    }
    [Theory]
    [InlineData("192.0.2.10",200)]
    [InlineData("198.51.100.10",400)]
    public async Task PublicIngressOnlyTrustsExplicitSourceNetwork(string address,int expected)
    {
        await using var f=new PortalFactory{PublicOrigin="https://portal.example.test",ProxyAddress=IPAddress.Parse(address),TrustedProxyNetworks="192.0.2.0/24"};
        using var c=f.CreateClient(new(){BaseAddress=new Uri("http://portal.example.test")});c.DefaultRequestHeaders.Add("X-Forwarded-Proto","https");
        var response=await c.GetAsync("/api/session");Assert.Equal(expected,(int)response.StatusCode);
        if(expected==200)Assert.All(response.Headers.GetValues("Set-Cookie"),cookie=>Assert.Contains("secure",cookie,StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public async Task PublicOriginRejectsHttpWrongHostAndCrossOriginWrites()
    {
        await using var f=new PortalFactory{PublicOrigin="https://portal.example.test"};using var c=f.CreateClient(new(){BaseAddress=new Uri("https://portal.example.test")});
        Assert.Equal(HttpStatusCode.BadRequest,(await c.GetAsync("http://portal.example.test/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.GetAsync("https://wrong.example.test/api/session")).StatusCode);
        await PortalFactory.RefreshCsrf(c);c.DefaultRequestHeaders.Add("Origin","https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync("/api/login",new{username="alice",password=PortalFactory.Password})).StatusCode);
        c.DefaultRequestHeaders.Remove("Origin");c.DefaultRequestHeaders.Add("Origin","https://portal.example.test");
        var login=await c.PostAsJsonAsync("/api/login",new{username="alice",password=PortalFactory.Password});login.EnsureSuccessStatusCode();
        Assert.All(login.Headers.GetValues("Set-Cookie"),cookie=>{Assert.Contains("secure",cookie,StringComparison.OrdinalIgnoreCase);Assert.Contains("httponly",cookie,StringComparison.OrdinalIgnoreCase);});
    }
    [Fact] public async Task DemoStylesDoNotWeakenConnectedPageScriptOrStylePolicy()
    {
        await using var f=new PortalFactory();File.WriteAllText(Path.Combine(f.Root,"live.html"),"Live");File.WriteAllText(Path.Combine(f.Root,"index.html"),"Demo");using var c=f.CreateClient();
        var live=await c.GetAsync("/");live.EnsureSuccessStatusCode();var demo=await c.GetAsync("/demo");demo.EnsureSuccessStatusCode();
        var livePolicy=live.Headers.GetValues("Content-Security-Policy").Single();var demoPolicy=demo.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("style-src-attr 'none'",livePolicy);Assert.DoesNotContain("unsafe-inline",livePolicy);
        Assert.Contains("style-src-attr 'unsafe-inline'",demoPolicy);Assert.Contains("script-src 'self';",demoPolicy);
    }
    [Fact] public async Task InvalidGithubCallbackHasSafeRecoveryPageNotSecrets()
    {await using var f=new PortalFactory();using var c=f.CreateClient();var r=await c.GetAsync("/github/setup/callback?state=invalid&code=test-only-invalid-code");Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);var body=await r.Content.ReadAsStringAsync();Assert.Contains("Return to Connections",body);Assert.DoesNotContain("test-only-invalid-code",body);Assert.Equal("no-store",r.Headers.CacheControl!.ToString());}
    [Fact] public async Task AnonymousCannotListRuns(){await using var f=new PortalFactory();using var c=f.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/runs")).StatusCode);}
    [Fact] public async Task LoginRequiresCsrf(){await using var f=new PortalFactory();using var c=f.CreateClient();Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/login",new{username="alice",password=PortalFactory.Password})).StatusCode);}
    [Fact] public async Task InvalidPasswordFails(){await using var f=new PortalFactory();using var c=f.CreateClient();await PortalFactory.RefreshCsrf(c);Assert.Equal(HttpStatusCode.Unauthorized,(await c.PostAsJsonAsync("/api/login",new{username="alice",password="wrong"})).StatusCode);}
    [Fact] public async Task WriteRequiresCsrfAfterLogin(){await using var f=new PortalFactory();using var c=await f.Login();c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/runs",new{title="x",instructions="x",template="environment-check"})).StatusCode);}
    [Fact] public async Task OwnershipCoversDetailsActionsAndStreams()
    {
        await using var f=new PortalFactory();using var alice=await f.Login();using var bob=await f.Login("bob");var run=await PortalFactory.Create(alice);await PortalFactory.Wait(alice,run.Id,"review");
        Assert.Empty((await bob.GetFromJsonAsync<Run[]>("/api/runs"))!);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync("/api/runs/"+run.Id)).StatusCode);
        PortalFactory.Key(bob);Assert.Equal(HttpStatusCode.NotFound,(await bob.PostAsJsonAsync($"/api/runs/{run.Id}/cancel",new{message=""})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync($"/api/runs/{run.Id}/events")).StatusCode);
    }
    [Fact] public async Task BrowserCannotSelectOwnerOrImage()
    {
        await using var f=new PortalFactory();using var c=await f.Login();PortalFactory.Key(c);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/runs",new{title="x",instructions="x",template="environment-check",owner="bob",image="evil"})).StatusCode);
    }
    [Fact] public async Task UnknownTemplateIsRejected(){await using var f=new PortalFactory();using var c=await f.Login();PortalFactory.Key(c);Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/runs",new{title="x",instructions="x",template="codex"})).StatusCode);}
    [Fact] public async Task MissingIdempotencyKeyIsRejected(){await using var f=new PortalFactory();using var c=await f.Login();Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/runs",new{title="x",instructions="x",template="environment-check"})).StatusCode);}
    [Fact] public async Task DuplicateCreateDoesNotExecuteTwice()
    {
        await using var f=new PortalFactory();using var c=await f.Login();PortalFactory.Key(c);var input=new{title="x",instructions="x",template="environment-check"};
        var a=(await (await c.PostAsJsonAsync("/api/runs",input)).Content.ReadFromJsonAsync<Run>())!;
        var b=(await (await c.PostAsJsonAsync("/api/runs",input)).Content.ReadFromJsonAsync<Run>())!;
        Assert.Equal(a.Id,b.Id);await PortalFactory.Wait(c,a.Id,"review");Assert.Equal(1,f.Platform.Executions);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/runs",new{title="different",instructions="x",template="environment-check"})).StatusCode);
    }
    [Fact] public async Task FailureIsNotSuccessAndReleaseIsConfirmed()
    {
        await using var f=new PortalFactory();using var c=await f.Login();var run=await PortalFactory.Create(c,"failure-check");run=await PortalFactory.Wait(c,run.Id,"failed");Assert.Equal("Suspended",run.PlatformPhase);Assert.Contains("7",run.Error);
    }
    [Fact] public async Task ResumeAddsTurnAndFinishIsTerminal()
    {
        await using var f=new PortalFactory();using var c=await f.Login();var run=await PortalFactory.Create(c);run=await PortalFactory.Wait(c,run.Id,"review");Assert.Equal(1,run.Turn);
        PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{run.Id}/resume",new{message="Continue"})).EnsureSuccessStatusCode();run=await PortalFactory.Wait(c,run.Id,"review");Assert.Equal(2,run.Turn);
        PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{run.Id}/finish",new{message=""})).EnsureSuccessStatusCode();run=await PortalFactory.Wait(c,run.Id,"completed");Assert.Equal("Deleted",run.PlatformPhase);
        PortalFactory.Key(c);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/runs/{run.Id}/resume",new{message="again"})).StatusCode);
    }
    [Fact] public async Task RunningCommandCanBeCancelled()
    {
        await using var f=new PortalFactory();f.Platform.WaitForCancel=true;using var c=await f.Login();var run=await PortalFactory.Create(c);await PortalFactory.Wait(c,run.Id,"running");
        PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{run.Id}/cancel",new{message=""})).EnsureSuccessStatusCode();run=await PortalFactory.Wait(c,run.Id,"cancelled");Assert.Equal("Deleted",run.PlatformPhase);
    }
    [Fact] public async Task FailedReleaseIsNotShownAsSuspended()
    {
        await using var f=new PortalFactory();f.Platform.RefuseSuspend=true;using var c=await f.Login();var run=await PortalFactory.Create(c);run=await PortalFactory.Wait(c,run.Id,"recovery-required");Assert.Equal("Unknown",run.PlatformPhase);
    }
    [Fact] public async Task EventsAreDurableAndOwnerChecked()
    {
        await using var f=new PortalFactory();using var c=await f.Login();var run=await PortalFactory.Create(c);await PortalFactory.Wait(c,run.Id,"review");
        var store=new RunStore(new(){DataDirectory=f.Root});var events=store.Events("alice",run.Id,0);Assert.Contains(events,e=>e.Kind=="stdout");Assert.Contains(events,e=>e.Kind=="exit");
        var after=events[1].Id;Assert.All(store.Events("alice",run.Id,after),e=>Assert.True(e.Id>after));Assert.Throws<PortalException>(()=>store.Events("bob",run.Id,0));
    }
    [Fact] public async Task SseReplaysEventsWithIds()
    {
        await using var f=new PortalFactory();using var c=await f.Login();var run=await PortalFactory.Create(c);await PortalFactory.Wait(c,run.Id,"review");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var response=await c.SendAsync(new(HttpMethod.Get,$"/api/runs/{run.Id}/events"),HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        Assert.Equal("text/event-stream",response.Content.Headers.ContentType?.MediaType);using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));Assert.StartsWith("id: ",await reader.ReadLineAsync(timeout.Token));
    }
    [Fact] public async Task InterruptedWorkIsReleasedWithoutCommandReplay()
    {
        await using var f=new PortalFactory();
        var store=new RunStore(new(){DataDirectory=f.Root});
        var run=store.Create("alice",new("Interrupted","test","environment-check"),Guid.NewGuid().ToString());
        Assert.NotNull(store.Claim());
        using var c=await f.Login();run=await PortalFactory.Wait(c,run.Id,"failed");
        Assert.Equal("Suspended",run.PlatformPhase);Assert.Equal(0,f.Platform.Executions);
        Assert.Empty(store.Interrupted());
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("finish")]
    [InlineData("suspend")]
    public async Task FailedControlActionIsVisibleAndRetryable(string action)
    {
        await using var f=new PortalFactory();using var c=await f.Login();var run=await PortalFactory.Create(c);await PortalFactory.Wait(c,run.Id,"review");
        f.Platform.RefuseDelete=action!="suspend";f.Platform.RefuseSuspend=action=="suspend";
        PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{run.Id}/{action}",new{message=""})).EnsureSuccessStatusCode();
        run=await PortalFactory.Wait(c,run.Id,"recovery-required");Assert.Contains(action,run.Error);
        var blocked=await PortalFactory.Create(c);await Task.Delay(400);Assert.Equal("queued",(await c.GetFromJsonAsync<Run>("/api/runs/"+blocked.Id))!.State);
        f.Platform.RefuseDelete=false;f.Platform.RefuseSuspend=false;
        PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{run.Id}/cancel",new{message=""})).EnsureSuccessStatusCode();
        await PortalFactory.Wait(c,run.Id,"cancelled");await PortalFactory.Wait(c,blocked.Id,"review");
    }
    [Fact] public void QueuePersistsAcrossStoreInstances()
    {
        var path=Path.Combine(Path.GetTempPath(),"agent-desk-store-"+Guid.NewGuid());
        var options=new PortalOptions{DataDirectory=path};
        var first=new RunStore(options);var run=first.Create("alice",new("Queued","test","environment-check"),Guid.NewGuid().ToString());
        var second=new RunStore(options);Assert.Equal(run.Id,second.Claim()!.RunId);Assert.Equal("alice",second.Get(run.Id)!.Owner);
    }

}
