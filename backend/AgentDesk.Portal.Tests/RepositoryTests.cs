using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDesk.Portal;
using Xunit;
namespace AgentDesk.Portal.Tests;
public sealed class FakeRepositoryWorkflow : IRepositoryWorkflow
{
    public int Prepares,Captures,Publishes,Cleanups;public bool Block,FailPublish;public TaskCompletionSource? Publishing,Release;
    public Task Prepare(Run run,CancellationToken ct){Prepares++;return Task.CompletedTask;}
    public Task<GitPublication> Capture(Run run,CancellationToken ct){Captures++;return Task.FromResult(new GitPublication(Block?"blocked":"ready",Block?null:new string('b',40),"diff","checks",run.Turn));}
    public async Task Publish(Run run,CancellationToken ct){Publishes++;Publishing?.TrySetResult();if(Release is not null)await Release.Task.WaitAsync(ct);if(FailPublish)throw new IOException("uncertain");}
    public void Cleanup(Run run)=>Cleanups++;
}
public sealed class RepositoryLifecycleTests
{
    internal static Run Create(RunStore store,string mode="manual")=>store.Create("alice",new("Repo test","Make a small change","codex-task"){Repository=new(123,"main","python3 -m unittest",mode)},Guid.NewGuid().ToString(),"codex",new("github",123,"example-owner/test","main",new string('a',40),"python3 -m unittest",mode));
    [Theory][InlineData("manual",false,0,0)][InlineData("auto",false,0,1)][InlineData("auto",true,0,0)][InlineData("auto",false,7,0)]
    public async Task OnlyExplicitAutoAfterSuccessfulTurnAndChecksPublishes(string mode,bool blocked,int exit,int publishes)
    {
        var flow=new FakeRepositoryWorkflow{Block=blocked};await using var f=new PortalFactory{Repositories=flow};f.Platform.ExitCode=exit;var store=new RunStore(new(){DataDirectory=f.Root});var run=Create(store,mode);using var c=await f.Login();run=await PortalFactory.Wait(c,run.Id,"review","failed");Assert.Equal(publishes,flow.Publishes);Assert.Equal(exit==0?1:0,flow.Captures);Assert.Equal("Suspended",run.PlatformPhase);
    }
    [Fact] public async Task ManualPublishRequiresOwnerAndExactCheckedCommitAndIsIdempotent()
    {
        var flow=new FakeRepositoryWorkflow{Publishing=new(),Release=new()};await using var f=new PortalFactory{Repositories=flow};var store=new RunStore(new(){DataDirectory=f.Root});var r=Create(store);using var c=await f.Login();using var bob=await f.Login("bob");r=await PortalFactory.Wait(c,r.Id,"review");
        PortalFactory.Key(bob);Assert.Equal(HttpStatusCode.NotFound,(await bob.PostAsJsonAsync($"/api/runs/{r.Id}/publish",new{message=r.Publication!.Head})).StatusCode);
        PortalFactory.Key(c);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/runs/{r.Id}/publish",new{message="stale"})).StatusCode);
        PortalFactory.Key(c);var body=new{message=r.Publication!.Head};(await c.PostAsJsonAsync($"/api/runs/{r.Id}/publish",body)).EnsureSuccessStatusCode();await flow.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));(await c.PostAsJsonAsync($"/api/runs/{r.Id}/publish",body)).EnsureSuccessStatusCode();
        foreach(var action in new[]{"cancel","resume","finish","suspend","browse"}){PortalFactory.Key(c);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/runs/{r.Id}/{action}",new{message=""})).StatusCode);}
        flow.Release.SetResult();await PortalFactory.Wait(c,r.Id,"review");Assert.Equal(1,flow.Publishes);Assert.Equal(1,f.Platform.Executions);
    }
    [Fact] public async Task UncertainPublishRequiresManualRetryNotAnotherAiTurn()
    {
        var flow=new FakeRepositoryWorkflow{FailPublish=true};await using var f=new PortalFactory{Repositories=flow};var store=new RunStore(new(){DataDirectory=f.Root});var r=Create(store,"auto");using var c=await f.Login();r=await PortalFactory.Wait(c,r.Id,"review");Assert.Equal("failed",r.Publication!.State);await Task.Delay(350);Assert.Equal(1,flow.Publishes);flow.FailPublish=false;PortalFactory.Key(c);(await c.PostAsJsonAsync($"/api/runs/{r.Id}/publish",new{message=r.Publication.Head})).EnsureSuccessStatusCode();await PortalFactory.Wait(c,r.Id,"review");Assert.Equal(2,flow.Publishes);Assert.Equal(1,f.Platform.Executions);
    }
    [Fact] public async Task RestartedPublicationIsNotReplayed()
    {
        var flow=new FakeRepositoryWorkflow();await using var f=new PortalFactory{Repositories=flow};var store=new RunStore(new(){DataDirectory=f.Root});var r=Create(store);store.Complete(store.Claim()!.Id);store.Update(r.Id,x=>x with{State="review",Turn=1,PlatformPhase="Suspended",Publication=new("ready",new string('b',40),CheckedTurn:1)});store.QueueAction("alice",r.Id,"publish",new string('b',40),Guid.NewGuid().ToString());Assert.Equal("publish",store.Claim()!.Kind);using var c=await f.Login();r=await PortalFactory.Wait(c,r.Id,"review");Assert.Equal("failed",r.Publication!.State);Assert.Equal(0,flow.Publishes);Assert.Equal(0,f.Platform.Executions);
    }
    [Fact] public void ResumeInvalidatesOldCandidateBeforeWorkStarts()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{var store=new RunStore(new(){DataDirectory=path});var r=Create(store);store.Complete(store.Claim()!.Id);store.Update(r.Id,x=>x with{State="review",Turn=1,Publication=new("ready",new string('b',40),CheckedTurn:1)});r=store.QueueAction("alice",r.Id,"resume","again",Guid.NewGuid().ToString());Assert.Null(r.Publication!.Head);Assert.Equal("not-ready",r.Publication.State);}finally{Directory.Delete(path,true);}
    }
}
public sealed class FakeTrustedGit : ITrustedGit
{
    public List<(string[] Args,string? Token,bool Files)> Calls=[];public Func<string[],string> Reply=_=>"";
    public Task<string> Run(string cwd,IEnumerable<string> args,string? token,CancellationToken ct,bool allowFile=false){var a=args.ToArray();Calls.Add((a,token,allowFile));return Task.FromResult(Reply(a));}
}
public sealed class RepositoryPublishTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"repo-publish-"+Guid.NewGuid());private readonly RunStore store;private readonly FakeTrustedGit git=new();private readonly FakeGithubApi api=new();private readonly RepositoryWorkflow workflow;private readonly Run run;private string? remote;private bool existing,closed,prFailure;private readonly string head=new('b',40);
    public RepositoryPublishTests()
    {
        Directory.CreateDirectory(root);File.WriteAllBytes(Path.Combine(root,"key"),RandomNumberGenerator.GetBytes(32));var options=new PortalOptions{DataDirectory=root,CodexVaultKey=Path.Combine(root,"key"),GithubEnabled=true};store=new(options);var vault=new GithubVault(options);using var rsa=RSA.Create(2048);vault.Save(new("alice","github",1,"lab",rsa.ExportPkcs8PrivateKeyPem(),42,"example-owner"));var connections=new GithubConnections(options,vault,api);workflow=new(options,connections,api,new FakePlatform(),store,git);run=RepositoryLifecycleTests.Create(store);store.Complete(store.Claim()!.Id);store.Update(run.Id,r=>r with{Turn=1,State="publishing",Publication=new("publishing",head,CheckedTurn:1)});run=store.Get(run.Id)!;
        git.Reply=a=>{if(a[0]=="push")remote=head;return "";};api.Reply=(method,path,token,body)=>{
            if(path.EndsWith("access_tokens"))return JsonSerializer.SerializeToElement(new{token="test-ephemeral-token"});
            if(path.StartsWith("/app/installations"))return JsonSerializer.SerializeToElement(new{repository_selection="selected",suspended_at=(string?)null});
            if(path=="/repositories/123")return JsonSerializer.SerializeToElement(new{full_name="example-owner/test",archived=false,disabled=false});
            if(path.Contains("/git/ref/")){if(remote is null)throw new PortalException(404,"missing");return JsonSerializer.SerializeToElement(new{@object=new{sha=remote}});}
            if(path.Contains("/pulls?") )return JsonSerializer.SerializeToElement(existing?new[]{Pr()}:Array.Empty<object>());
            if(path.EndsWith("/pulls")&&method=="POST"){if(prFailure)throw new IOException("uncertain PR creation");existing=true;return JsonSerializer.SerializeToElement(Pr());}
            throw new Exception("Unexpected request "+path);
        };
    }
    private object Pr()=>new{number=7,html_url="https://github.com/example-owner/test/pull/7",state=closed?"closed":"open",head=new{@ref=run.PublishBranch,repo=new{id=123}}};
    public void Dispose()=>Directory.Delete(root,true);
    [Fact] public async Task PushesOnlyAssignedBranchWithoutForceAndCreatesDraft()
    {await workflow.Publish(run,default);var push=Assert.Single(git.Calls,x=>x.Args[0]=="push");Assert.Equal(head+":refs/heads/"+run.PublishBranch,push.Args.Last());Assert.DoesNotContain(push.Args,x=>x.Contains("force"));Assert.Equal("test-ephemeral-token",push.Token);var request=api.Calls.Single(x=>x.Method=="POST"&&x.Path.EndsWith("/pulls"));var body=JsonSerializer.SerializeToElement(request.Body);Assert.True(body.GetProperty("draft").GetBoolean());Assert.Equal("published",store.Get(run.Id)!.Publication!.State);Assert.DoesNotContain(api.Calls,x=>x.Path.Contains("/merge"));}
    [Fact] public async Task RetryReusesSameBranchAndPrWithoutPushingAgain()
    {remote=head;existing=true;await workflow.Publish(run,default);Assert.DoesNotContain(git.Calls,x=>x.Args[0]=="push");Assert.DoesNotContain(api.Calls,x=>x.Method=="POST"&&x.Path.EndsWith("/pulls"));Assert.Equal(7,store.Get(run.Id)!.Publication!.PullRequestNumber);Assert.Contains(store.Events("alice",run.Id,0),e=>e.Text.Contains("no push was needed"));}
    [Fact] public async Task ExternalHeadIsNeverOverwritten()
    {remote=new string('c',40);await Assert.ThrowsAsync<PortalException>(()=>workflow.Publish(run,default));Assert.DoesNotContain(git.Calls,x=>x.Args[0]=="push");}
    [Fact] public async Task ClosedOrMergedPrBlocksPublishing()
    {existing=true;closed=true;await Assert.ThrowsAsync<PortalException>(()=>workflow.Publish(run,default));Assert.DoesNotContain(git.Calls,x=>x.Args[0]=="push");}
    [Fact] public async Task DeletedPublishedBranchIsNotRecreated()
    {await Assert.ThrowsAsync<PortalException>(()=>workflow.Publish(run with{Publication=run.Publication! with{LastPublishedSha=head}},default));Assert.DoesNotContain(git.Calls,x=>x.Args[0]=="push");}
    [Fact] public async Task FailedPrCreationRetainsPushedShaAndRetryDoesNotPushAgain()
    {prFailure=true;await Assert.ThrowsAsync<IOException>(()=>workflow.Publish(run,default));Assert.Equal(head,store.Get(run.Id)!.Publication!.LastPublishedSha);prFailure=false;await workflow.Publish(store.Get(run.Id)!,default);Assert.Single(git.Calls,x=>x.Args[0]=="push");}
    [Fact] public async Task StaleCheckCannotPublish()
    {await Assert.ThrowsAsync<PortalException>(()=>workflow.Publish(run with{Turn=2},default));Assert.Empty(api.Calls);}
}
public sealed class TrustedGitTests
{
    [Fact] public async Task RealGitIgnoresHooksAndExportsImportableBundleWithoutCredentials()
    {
        var root=Path.Combine(Path.GetTempPath(),"trusted-git-"+Guid.NewGuid());try{
            var git=new TrustedGit(new(){DataDirectory=root});var cwd=Path.Combine(root,"repositories","fixture");Directory.CreateDirectory(cwd);await git.Run(cwd,["init"],null,default);Directory.CreateDirectory(Path.Combine(cwd,".git","hooks"));var hook=Path.Combine(cwd,".git","hooks","pre-commit");await File.WriteAllTextAsync(hook,"#!/bin/sh\ntouch ../hook-ran\nexit 1\n");if(!OperatingSystem.IsWindows())File.SetUnixFileMode(hook,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(cwd,"hello.txt"),"hello");await git.Run(cwd,["add","."],null,default);await git.Run(cwd,["-c","user.name=Fixture","-c","user.email=fixture@example.test","commit","-m","fixture"],"secret-test-token",default);Assert.False(File.Exists(Path.Combine(cwd,"..","hook-ran")));var head=await git.Run(cwd,["rev-parse","HEAD"],null,default);var bundle=Path.Combine(root,"fixture.bundle");await git.Run(cwd,["bundle","create",bundle,"HEAD"],null,default);
            var bare=Path.Combine(root,"repositories","import");await git.Run(bare,["init","--bare"],null,default);await git.Run(bare,["fetch",bundle,"HEAD"],null,default,true);Assert.Equal(head,await git.Run(bare,["rev-parse","FETCH_HEAD"],null,default));Assert.DoesNotContain("secret-test-token",await File.ReadAllTextAsync(Path.Combine(cwd,".git","config")));Assert.DoesNotContain("secret-test-token",await File.ReadAllTextAsync(Path.Combine(root,"repositories","askpass.sh")));
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
