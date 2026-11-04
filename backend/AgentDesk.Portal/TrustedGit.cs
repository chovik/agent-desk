using System.Diagnostics;
using System.Text;
namespace AgentDesk.Portal;
public interface ITrustedGit { Task<string> Run(string cwd,IEnumerable<string> args,string? token,CancellationToken ct,bool allowFile=false); }
public sealed class TrustedGit(PortalOptions options) : ITrustedGit
{
    private string Root=>Path.Combine(options.DataDirectory,"repositories");
    private void Quota(){if(Directory.Exists(Root)&&Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length)>128L*1024*1024)throw new PortalException(409,"Repository storage reached the 128 MiB lab limit.");}
    public async Task<string> Run(string cwd,IEnumerable<string> args,string? token,CancellationToken ct,bool allowFile=false)
    {
        Quota();Directory.CreateDirectory(cwd);var helper=Path.Combine(Root,"askpass.sh");
        if(!File.Exists(helper)){await File.WriteAllTextAsync(helper,"#!/bin/sh\ncase \"$1\" in *Username*) printf '%s\\n' x-access-token ;; *) printf '%s\\n' \"$AGENT_DESK_GIT_TOKEN\" ;; esac\n",ct);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(helper,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}
        var info=new ProcessStartInfo("/usr/bin/prlimit"){WorkingDirectory=cwd,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in new[]{"--fsize=33554432","--cpu=90","--","/usr/bin/git","-c","core.hooksPath=/dev/null","-c","core.fsmonitor=false","-c","credential.helper=","-c","http.followRedirects=false","-c","protocol.allow=never","-c","protocol.https.allow=always","-c","protocol.file.allow="+(allowFile?"always":"never"),"-c","fetch.fsckObjects=true","-c","transfer.fsckObjects=true","-c","gc.auto=0","-c","pack.threads=1"})info.ArgumentList.Add(arg);
        if(!string.IsNullOrEmpty(options.CodexProxy)){info.ArgumentList.Add("-c");info.ArgumentList.Add("http.proxy="+options.CodexProxy);}foreach(var arg in args)info.ArgumentList.Add(arg);
        info.Environment.Clear();foreach(var pair in new Dictionary<string,string>{{"PATH","/usr/bin:/bin"},{"HOME",cwd},{"LANG","C.UTF-8"},{"GIT_CONFIG_NOSYSTEM","1"},{"GIT_CONFIG_GLOBAL","/dev/null"},{"GIT_TERMINAL_PROMPT","0"},{"GIT_NO_REPLACE_OBJECTS","1"},{"GIT_ASKPASS",helper}})info.Environment[pair.Key]=pair.Value;
        if(token is not null)info.Environment["AGENT_DESK_GIT_TOKEN"]=token;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(100));using var process=new Process{StartInfo=info};process.Start();
        async Task<string> Read(StreamReader reader){var text=new StringBuilder();var buffer=new char[4096];int n;while((n=await reader.ReadAsync(buffer,timeout.Token))>0){if(text.Length+n>256000){timeout.Cancel();throw new IOException("Git output limit exceeded.");}text.Append(buffer,0,n);}return text.ToString();}
        var output=Read(process.StandardOutput);var error=Read(process.StandardError);
        try{await Task.WhenAll(process.WaitForExitAsync(timeout.Token),output,error);Quota();if(process.ExitCode!=0)throw new IOException("Git operation failed. Check repository access, size limits and branch history.");return output.Result.Trim();}
        finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}try{await Task.WhenAll(output,error);}catch{}}
    }
}
