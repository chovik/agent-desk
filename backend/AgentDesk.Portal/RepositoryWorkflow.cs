using System.Text.Json;
using System.Text.RegularExpressions;
namespace AgentDesk.Portal;

public interface IRepositoryWorkflow
{
    Task Prepare(Run run,CancellationToken ct);
    Task<GitPublication> Capture(Run run,CancellationToken ct);
    Task Publish(Run run,CancellationToken ct);
    void Cleanup(Run run);
}
// Only fixed Git plumbing runs in this trusted process. Never execute repository code,
// copy guest .git/config/hooks, follow redirects, or inherit a host credential helper.
public sealed class RepositoryWorkflow(PortalOptions options,GithubConnections connections,IGithubApi api,IExecutionPlatform platform,RunStore store,ITrustedGit git) : IRepositoryWorkflow
{
    private const int BundleLimit=8*1024*1024;
    private string Root=>Path.Combine(options.DataDirectory,"repositories");
    private string DirectoryFor(Run run){if(!Regex.IsMatch(run.Id,"^[a-f0-9]{32}$"))throw new IOException("Invalid run ID.");Directory.CreateDirectory(Root);var dir=Path.Combine(Root,run.Id);Directory.CreateDirectory(dir);return dir;}
    private static string Sha(string? value)=>Regex.IsMatch(value??"","^[a-f0-9]{40}$")?value!:throw new IOException("Invalid Git commit ID.");
    private static string Url(Run run)=>GithubConnections.SafeRepo(run.Repository!.FullName)?"https://github.com/"+run.Repository.FullName+".git":throw new IOException("Invalid repository.");
    private Task<string> Git(string cwd,IEnumerable<string> args,string? token,CancellationToken ct,bool allowFile=false)=>git.Run(cwd,args,token,ct,allowFile);
    public void Cleanup(Run run){if(run.Repository is null)return;var dir=DirectoryFor(run);Directory.Delete(dir,true);}
    private async Task CheckRepository(Run run,string token,CancellationToken ct)
    {
        var r=await api.Send("GET","/repositories/"+run.Repository!.RepositoryId,token,null,ct);
        if(r.GetProperty("full_name").GetString()!=run.Repository.FullName||r.GetProperty("archived").GetBoolean()||r.GetProperty("disabled").GetBoolean())throw new PortalException(409,"Repository was renamed, moved, archived or disabled. Start a new run.");
    }
    public async Task Prepare(Run run,CancellationToken ct)
    {
        if(run.Repository is null||run.RepositoryPrepared)return;
        var dir=DirectoryFor(run);var bare=Path.Combine(dir,"git");
        using var access=await connections.Acquire(run.Owner,run.Repository.ConnectionId,run.Repository.RepositoryId,false,ct);await CheckRepository(run,access.Token,ct);
        await Git(bare,["init","--bare"],null,ct);await Git(bare,["fetch","--no-tags",Url(run),Sha(run.Repository.BaseSha)],access.Token,ct);
        await Git(bare,["update-ref","refs/heads/"+run.PublishBranch,run.Repository.BaseSha],null,ct);
        var bundle=Path.Combine(dir,"seed.bundle");if(File.Exists(bundle))File.Delete(bundle);await Git(bare,["bundle","create",bundle,"refs/heads/"+run.PublishBranch],null,ct);
        if(new FileInfo(bundle).Length>BundleLimit)throw new PortalException(409,"Repository exceeds the 8 MiB checkout bundle limit.");
        await platform.InstallRepositoryAsync(run,await File.ReadAllBytesAsync(bundle,ct),ct);File.Delete(bundle);store.Update(run.Id,r=>r with{RepositoryPrepared=true},"Repository checked out at the pinned commit in /workspace/repo. No GitHub credentials entered the sandbox.");
    }
    public async Task<GitPublication> Capture(Run run,CancellationToken ct)
    {
        var previous=run.Publication??new();var capture=await platform.CaptureRepositoryAsync(run,ct);
        if(!capture.Ok)return previous with{State="blocked",Head=null,Diff="",Checks=capture.Checks,CheckedTurn=run.Turn,Error=capture.Error};
        var head=Sha(capture.Head);var dir=DirectoryFor(run);var bare=Path.Combine(dir,"git");var candidate=Path.Combine(dir,"candidate.bundle");
        if(capture.Bundle.Length>BundleLimit)throw new IOException("Publication bundle exceeds limit.");
        await File.WriteAllBytesAsync(candidate+".tmp",capture.Bundle,ct);File.Move(candidate+".tmp",candidate,true);
        await Git(bare,["bundle","verify",candidate],null,ct);await Git(bare,["fetch","--no-tags",candidate,"HEAD"],null,ct,true);
        if(await Git(bare,["rev-parse","FETCH_HEAD"],null,ct)!=head)throw new IOException("Bundle HEAD does not match reviewed HEAD.");
        await Git(bare,["merge-base","--is-ancestor",Sha(run.Repository!.BaseSha),head],null,ct);
        if(previous.LastPublishedSha is not null)await Git(bare,["merge-base","--is-ancestor",Sha(previous.LastPublishedSha),head],null,ct);
        var paths=await Git(bare,["diff","--name-only",run.Repository.BaseSha,head,"--",".github/workflows"],null,ct);
        if(paths.Length>0)throw new PortalException(409,"Workflow changes are not publishable by this App integration.");
        return previous with{State="ready",Head=head,Diff=await Git(bare,["diff","--no-ext-diff","--no-textconv","--no-color",run.Repository.BaseSha,head],null,ct),Checks=capture.Checks,CheckedTurn=run.Turn,Error=null};
    }
    public async Task Publish(Run run,CancellationToken ct)
    {
        var binding=run.Repository??throw new PortalException(409,"This run has no repository.");var review=run.Publication??throw new PortalException(409,"No reviewed commit is available.");
        if(review.CheckedTurn!=run.Turn||review.Head is null||review.State is not ("ready" or "failed" or "publishing"))throw new PortalException(409,"A successful checked commit is required.");
        var head=Sha(review.Head);var bare=Path.Combine(DirectoryFor(run),"git");using var access=await connections.Acquire(run.Owner,binding.ConnectionId,binding.RepositoryId,true,ct);await CheckRepository(run,access.Token,ct);
        await Git(bare,["cat-file","-e",head+"^{commit}"],null,ct);await Git(bare,["merge-base","--is-ancestor",binding.BaseSha,head],null,ct);
        string? remote=null;try{var r=await api.Send("GET",$"/repos/{binding.FullName}/git/ref/heads/{Uri.EscapeDataString(run.PublishBranch)}",access.Token,null,ct);remote=Sha(r.GetProperty("object").GetProperty("sha").GetString());}catch(PortalException ex)when(ex.Status==404){}
        if(remote is not null&&remote!=head&&remote!=review.LastPublishedSha)throw new PortalException(409,"The remote run branch changed externally. It will not be overwritten.");
        if(remote is null&&review.LastPublishedSha is not null)throw new PortalException(409,"The published branch was deleted. Start a new run rather than recreating it silently.");
        if(remote is not null&&remote!=head)await Git(bare,["merge-base","--is-ancestor",remote,head],null,ct);
        var prs=await api.Send("GET",$"/repos/{binding.FullName}/pulls?state=all&head={Uri.EscapeDataString(binding.FullName.Split('/')[0]+":"+run.PublishBranch)}&base={Uri.EscapeDataString(binding.BaseBranch)}&per_page=100",access.Token,null,ct);
        var matches=prs.EnumerateArray().Where(x=>x.GetProperty("head").GetProperty("ref").GetString()==run.PublishBranch&&x.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64()==binding.RepositoryId).ToArray();
        if(matches.Length>1||matches.Any(p=>p.GetProperty("state").GetString()!="open"))throw new PortalException(409,"This run's PR was closed or merged. Start a new run.");
        if(remote!=head)await Git(bare,["push","--porcelain",Url(run),head+":refs/heads/"+run.PublishBranch],access.Token,ct);
        var confirmed=await api.Send("GET",$"/repos/{binding.FullName}/git/ref/heads/{Uri.EscapeDataString(run.PublishBranch)}",access.Token,null,ct);
        if(confirmed.GetProperty("object").GetProperty("sha").GetString()!=head)throw new PortalException(409,"Remote branch changed during publication. No PR was created; inspect GitHub before retrying.");
        store.Update(run.Id,r=>r with{Publication=r.Publication! with{LastPublishedSha=head}},remote==head?"Run branch already has this checked commit; no push was needed.":"Run branch pushed without force. Preparing the draft PR link.");
        JsonElement pr=matches.Length==1?matches[0]:await api.Send("POST",$"/repos/{binding.FullName}/pulls",access.Token,new{title=run.Title,head=run.PublishBranch,@base=binding.BaseBranch,draft=true,maintainer_can_modify=false,body=$"Agent Desk run `{run.Id}`.\n\nRequired local check passed for `{head}`. Review the diff and GitHub CI before merging.\n\nNo automatic merge is enabled."},ct);
        var number=pr.GetProperty("number").GetInt32();var url=pr.GetProperty("html_url").GetString();if(url!=$"https://github.com/{binding.FullName}/pull/{number}")throw new IOException("Unexpected pull request URL.");
        store.Update(run.Id,r=>r with{Publication=r.Publication! with{State="published",LastPublishedSha=head,PullRequestNumber=number,PullRequestUrl=url,Error=null}},matches.Length==1?"Existing pull request reused for the checked branch. Nothing was merged.":"Draft pull request created. Nothing was merged.");
    }
}
