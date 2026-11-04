using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentDesk.Portal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AgentDesk.Portal.Tests;
public sealed class FilesAndBrokerTests
{
    [Fact] public async Task SavedFilesAreOwnerBoundVersionedAndDoNotWakeSandbox()
    {
        await using var f=new PortalFactory();using var alice=await f.Login();using var bob=await f.Login("bob");
        var run=await PortalFactory.Create(alice);run=await PortalFactory.Wait(alice,run.Id,"review");
        var files=f.Services.GetRequiredService<ArtifactStore>();files.Save(run,[new("src/a.txt",Encoding.UTF8.GetBytes("<script>not executed</script>"))],2);
        var prefix="/api/runs/"+run.Id+"/files";var count=f.Platform.Executions;
        Assert.Contains("src/a.txt",await alice.GetStringAsync(prefix));
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync(prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync(prefix+"/download?path=src/a.txt&turn=1")).StatusCode);
        var download=await alice.GetAsync(prefix+"/download?path=src/a.txt&turn=1");Assert.Equal("application/octet-stream",download.Content.Headers.ContentType!.MediaType);Assert.Equal("attachment",download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(HttpStatusCode.BadRequest,(await alice.GetAsync(prefix+"/content?path=../secret&turn=1")).StatusCode);
        files.Save(run with{Turn=2},[new("src/a.txt",Encoding.UTF8.GetBytes("new"))],0);
        Assert.Equal(HttpStatusCode.NotFound,(await alice.GetAsync(prefix+"/download?path=src/a.txt&turn=1")).StatusCode);
        Assert.Equal(count,f.Platform.Executions);Assert.Equal("Suspended",(await alice.GetFromJsonAsync<Run>("/api/runs/"+run.Id))!.PlatformPhase);
    }
    [Theory] [InlineData("/etc/passwd")] [InlineData("../secret")] [InlineData("src/../../secret")] [InlineData(".agent-desk/auth.json")] [InlineData("a\\b")] [InlineData("a//b")]
    public void FilePathsRejectEscapesAndHiddenEntries(string path)=>Assert.False(ArtifactStore.SafePath(path));
    [Fact] public void ModelBridgeRestrictsRoutesModelsAndProviderResources()
    {
        Assert.ThrowsAny<Exception>(()=>ModelBridge.Validate("{\"model\":\"allowed\"}","/v1/files","allowed"));
        Assert.ThrowsAny<Exception>(()=>ModelBridge.Validate("{\"model\":\"other\"}","/v1/responses","allowed"));
        foreach(var field in new[]{"previous_response_id","conversation","container","mcp_servers"})
            Assert.ThrowsAny<Exception>(()=>ModelBridge.Validate("{\"model\":\"allowed\",\""+field+"\":\"private-id\"}","/v1/responses","allowed"));
        Assert.ThrowsAny<Exception>(()=>ModelBridge.Validate("{\"model\":\"allowed\",\"tools\":[{\"type\":\"web_search\"}]}","/v1/responses","allowed"));
        var ok=ModelBridge.Validate("{\"model\":\"allowed\",\"store\":true,\"background\":true}","/v1/responses","allowed");Assert.False(ok["store"]!.GetValue<bool>());Assert.True(ok["stream"]!.GetValue<bool>());Assert.Null(ok["background"]);
    }
    [Fact] public async Task AccountLeasePreventsReconnectionAndDisconnectCancelsIt()
    {
        await using var f=new PortalFactory{EnableCodex=true};using var client=await f.Login();
        var payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":"+DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()+"}")).TrimEnd('=').Replace('+','-').Replace('/','_');
        var vault=f.Services.GetRequiredService<CodexVault>();vault.Save(new("bound","alice","account",null,null,"{\"OPENAI_API_KEY\":null,\"tokens\":{\"account_id\":\"account\",\"access_token\":\"e30."+payload+".fake\",\"refresh_token\":\"fake\"}}","now"));
        var connections=f.Services.GetRequiredService<CodexConnections>();await using var lease=await connections.Acquire("alice","bound",CancellationToken.None);
        Assert.Throws<PortalException>(()=>connections.CurrentConnection("alice","another"));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/connections/codex/login",new{})).StatusCode);
        (await client.PostAsJsonAsync("/api/connections/codex/disconnect",new{})).EnsureSuccessStatusCode();Assert.True(lease.Stop.IsCancellationRequested);Assert.Null(vault.Get("alice"));
    }
}
