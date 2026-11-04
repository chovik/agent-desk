using System.Text.Json;
namespace AgentDesk.Portal;
public sealed record RepositoryCapture(bool Ok,string? Head,string Diff,string Checks,string? Error,byte[] Bundle);
public sealed record GitPublication(string State="not-ready",string? Head=null,string Diff="",string Checks="",int CheckedTurn=0,string? LastPublishedSha=null,int? PullRequestNumber=null,string? PullRequestUrl=null,string? Error=null);
public sealed partial class AxExecutionPlatform
{
    private static async Task<string> RepositoryProgram(CancellationToken ct){using var input=typeof(AxExecutionPlatform).Assembly.GetManifestResourceStream("repository-helper")!;using var reader=new StreamReader(input);return await reader.ReadToEndAsync(ct);}
    public async Task InstallRepositoryAsync(Run run,byte[] bundle,CancellationToken ct)
    {
        if(bundle.Length>8*1024*1024)throw new IOException("Repository bundle is too large.");bool ready=false,installed=false;
        var exit=await Frames(run,["python3","-u","-c",await RepositoryProgram(ct)],new{mode="install",branch=run.PublishBranch,baseSha=run.Repository!.BaseSha},120,null,async(frame,send)=>
        {
            switch(frame.GetProperty("type").GetString()){
            case "ready":if(ready)throw new IOException("Duplicate repository request.");ready=true;for(var i=0;i<bundle.Length;i+=49152)await send(new{type="chunk",data=Convert.ToBase64String(bundle,i,Math.Min(49152,bundle.Length-i))});await send(new{type="end"});break;
            case "installed":installed=true;break;
            default:throw new IOException("Repository installation failed.");}
        },ct);if(exit!=0||!installed)throw new IOException("Repository installation was not confirmed.");
    }
    public async Task<RepositoryCapture> CaptureRepositoryAsync(Run run,CancellationToken ct)
    {
        bool? ok=null;string? head=null,reason=null;string diff="",checks="";bool ended=false;using var bundle=new MemoryStream();
        var exit=await Frames(run,["python3","-u","-c",await RepositoryProgram(ct)],new{mode="capture",branch=run.PublishBranch,baseSha=run.Repository!.BaseSha,checkCommand=run.Repository.CheckCommand},240,null,(frame,_)=>
        {
            switch(frame.GetProperty("type").GetString()){
            case "capture":if(ok is not null)throw new IOException("Duplicate repository result.");ok=frame.GetProperty("ok").GetBoolean();head=frame.TryGetProperty("head",out var h)?h.GetString():null;reason=frame.TryGetProperty("reason",out var r)?r.GetString():null;diff=frame.TryGetProperty("diff",out var d)?d.GetString()??"":"";checks=frame.TryGetProperty("checks",out var c)?c.GetString()??"":"";if(diff.Length>120000||checks.Length>32000)throw new IOException("Review output limit exceeded.");break;
            case "chunk":if(ok!=true||ended)throw new IOException("Unexpected repository bytes.");var data=Convert.FromBase64String(frame.GetProperty("data").GetString()!);if(bundle.Length+data.Length>8*1024*1024)throw new IOException("Repository bundle limit exceeded.");bundle.Write(data);break;
            case "end":if(ended||ok!=true||frame.GetProperty("bytes").GetInt64()!=bundle.Length)throw new IOException("Incomplete repository result.");ended=true;break;
            default:throw new IOException("Repository checks did not complete.");}return Task.CompletedTask;
        },ct);
        if(exit!=0||ok is null||ok==true&&(!ended||!System.Text.RegularExpressions.Regex.IsMatch(head??"","^[a-f0-9]{40}$")))throw new IOException("Repository verification was not confirmed.");return new(ok.Value,head,diff,checks,reason,bundle.ToArray());
    }
}
