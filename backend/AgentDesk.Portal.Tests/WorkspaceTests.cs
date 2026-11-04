using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentDesk.Portal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AgentDesk.Portal.Tests;
public sealed class WorkspaceTests
{
    private static async Task Action(HttpClient c,string id,string verb)
    {PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{id}/{verb}",new{})).EnsureSuccessStatusCode();}
    [Fact] public async Task BrowseIsExplicitOwnerBoundAndDoesNotExecuteOrCapture()
    {
        await using var f=new PortalFactory();using var a=await f.Login();using var b=await f.Login("bob");
        var r=await PortalFactory.Create(a);r=await PortalFactory.Wait(a,r.Id,"review");var url=$"/api/runs/{r.Id}";
        Assert.Equal(0,f.Platform.Captures);
        Assert.Equal(HttpStatusCode.Conflict,(await a.GetAsync(url+"/workspace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync(url+"/workspace")).StatusCode);
        PortalFactory.Key(b);Assert.Equal(HttpStatusCode.NotFound,(await b.PostAsJsonAsync(url+"/browse",new{})).StatusCode);
        a.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");PortalFactory.Key(a);Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync(url+"/browse",new{})).StatusCode);await PortalFactory.RefreshCsrf(a);
        var store=f.Services.GetRequiredService<RunStore>();store.Update(r.Id,x=>x with{ThreadId="retained-thread"});
        await Action(a,r.Id,"browse");var key=a.DefaultRequestHeaders.GetValues("Idempotency-Key").Single();
        r=await PortalFactory.Wait(a,r.Id,"browsing");Assert.Equal(1,r.Turn);Assert.Equal("retained-thread",r.ThreadId);Assert.NotNull(r.BrowseExpiresAt);
        PortalFactory.Key(a,key);(await a.PostAsJsonAsync(url+"/browse",new{})).EnsureSuccessStatusCode();
        Assert.Contains("a.txt",await a.GetStringAsync(url+"/workspace"));
        Assert.Equal("hello",await a.GetStringAsync(url+"/workspace/download?path=a.txt"));
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync(url+"/workspace/download?path=a.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync(url+"/workspace/content?path=../secret")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.GetAsync(url+"/workspace/content?path=.agent-desk/session")).StatusCode);
        Assert.Null((await a.GetFromJsonAsync<JsonElement>(url+"/files")).GetProperty("snapshot").Deserialize<FileSnapshot>());
        Assert.Equal(HttpStatusCode.NotFound,(await b.PostAsJsonAsync(url+"/workspace/export",new{})).StatusCode);
        (await a.PostAsJsonAsync(url+"/workspace/export",new{})).EnsureSuccessStatusCode();Assert.Equal(1,f.Platform.Captures);
        await Action(a,r.Id,"close-workspace");r=await PortalFactory.Wait(a,r.Id,"suspended");Assert.Equal("Suspended",r.PlatformPhase);Assert.Equal(1,r.Turn);Assert.Equal(1,f.Platform.Executions);
        Assert.Equal("hello",await a.GetStringAsync(url+"/files/download?path=a.txt&turn=1"));
    }
    [Fact] public async Task IdleBrowseSuspendsAndQueuedWorkCanReclaimTheSlot()
    {
        await using var f=new PortalFactory{BrowseIdleSeconds=1};using var c=await f.Login();var r=await PortalFactory.Create(c);await PortalFactory.Wait(c,r.Id,"review");
        await Action(c,r.Id,"browse");await PortalFactory.Wait(c,r.Id,"browsing");r=await PortalFactory.Wait(c,r.Id,"review");Assert.Equal("Suspended",r.PlatformPhase);Assert.Equal(1,f.Platform.Executions);Assert.Null(r.BrowseExpiresAt);
        await Action(c,r.Id,"browse");await PortalFactory.Wait(c,r.Id,"browsing");var next=await PortalFactory.Create(c);await PortalFactory.Wait(c,next.Id,"review");Assert.Equal("Suspended",(await c.GetFromJsonAsync<Run>($"/api/runs/{r.Id}"))!.PlatformPhase);
    }
    [Fact] public async Task ReadsDuringExecutionSerializeAgainstDeletion()
    {
        await using var f=new PortalFactory();f.Platform.WaitForCancel=true;f.Platform.Reading=new(TaskCreationOptions.RunContinuationsAsynchronously);f.Platform.ReleaseRead=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c=await f.Login();var r=await PortalFactory.Create(c);await PortalFactory.Wait(c,r.Id,"running");
        var read=c.GetAsync($"/api/runs/{r.Id}/workspace/download?path=a.txt");await f.Platform.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Action(c,r.Id,"cancel");await Task.Delay(300);Assert.Equal(0,f.Platform.Deletes);
        f.Platform.ReleaseRead.TrySetResult();(await read).EnsureSuccessStatusCode();r=await PortalFactory.Wait(c,r.Id,"cancelled");Assert.Equal("Deleted",r.PlatformPhase);Assert.Equal(1,f.Platform.Executions);
    }
    [Fact] public async Task InterruptedBrowseIsSuspendedWithoutAgentReplay()
    {
        await using var f=new PortalFactory();var store=new RunStore(new(){DataDirectory=f.Root});var r=store.Create("alice",new("Interrupted","test","environment-check"),Guid.NewGuid().ToString());store.Complete(store.Claim()!.Id);
        store.Update(r.Id,x=>x with{State="review",PlatformPhase="Suspended",Turn=3,ThreadId="retained"});store.QueueAction("alice",r.Id,"browse","",Guid.NewGuid().ToString());Assert.NotNull(store.Claim());store.Update(r.Id,x=>x with{State="browsing",PlatformPhase="Running",BrowseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(5)});
        using var c=await f.Login();r=await PortalFactory.Wait(c,r.Id,"failed");Assert.Equal("Suspended",r.PlatformPhase);Assert.Equal(3,r.Turn);Assert.Equal("retained",r.ThreadId);Assert.Equal(0,f.Platform.Executions);Assert.Null(r.BrowseExpiresAt);
    }
    [Fact] public async Task FailedWorkspaceCloseIsNotClaimedAsSuspended()
    {
        await using var f=new PortalFactory();using var c=await f.Login();var r=await PortalFactory.Create(c);await PortalFactory.Wait(c,r.Id,"review");await Action(c,r.Id,"browse");await PortalFactory.Wait(c,r.Id,"browsing");f.Platform.RefuseSuspend=true;
        await Action(c,r.Id,"close-workspace");r=await PortalFactory.Wait(c,r.Id,"recovery-required");Assert.Equal("Unknown",r.PlatformPhase);Assert.Equal(HttpStatusCode.Conflict,(await c.GetAsync($"/api/runs/{r.Id}/workspace")).StatusCode);
    }
}
