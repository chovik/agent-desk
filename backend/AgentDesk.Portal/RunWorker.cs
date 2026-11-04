using System.Collections.Concurrent;
namespace AgentDesk.Portal;

public sealed class RunWorker(RunStore store,IExecutionPlatform platform,RunLocks locks,PortalOptions options,ILogger<RunWorker> logger,IRepositoryWorkflow repositories) : BackgroundService
{
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _active=new();
    public void Interrupt(string id){if(_active.TryGetValue(id,out var cts))try{cts.Cancel();}catch(ObjectDisposedException){}}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never blindly repeat a command after a backend crash: reconcile/release it first.
        foreach(var job in store.Interrupted())
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var run=store.Get(job.RunId)!;
                    if(job.Kind=="publish")
                    {
                        PublicationFailed(run,"Backend restarted during publication. Check GitHub, then retry publishing the same checked commit; no automatic replay occurred.");
                    }
                    else if(job.Kind is "cancel" or "finish")
                    {
                        await Delete(run,stoppingToken);repositories.Cleanup(run);
                        store.Update(run.Id,r=>r with{State=job.Kind=="cancel"?"cancelled":"completed",PlatformPhase="Deleted"},"Interrupted release operation reconciled with AX.");
                    }
                    else
                    {
                        var phase=await Suspend(run,stoppingToken);
                        store.Update(run.Id,r=>r with{State="failed",PlatformPhase=phase,BrowseExpiresAt=null,Error="Backend restarted during work; automatic command replay was suppressed."},"Interrupted work reconciled. Resume explicitly to run another turn.");
                    }
                    store.Complete(job.Id);break;
                }
                catch(Exception ex) when(!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex,"Recovery pending for run {RunId}",job.RunId);
                    await Task.Delay(3000,stoppingToken);
                }
            }
        }
        while(!stoppingToken.IsCancellationRequested)
        {
            var job=store.Claim();
            if(job is null){await Task.Delay(250,stoppingToken);continue;}
            using var turn=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _active[job.RunId]=turn;
            try
            {
                await Execute(job,turn.Token,stoppingToken);
                store.Complete(job.Id);
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex)when(!stoppingToken.IsCancellationRequested)
            {
                var run=store.Get(job.RunId)!;
                if(job.Kind=="publish"){PublicationFailed(run,ex is PortalException?ex.Message:"Publication could not be confirmed. Check GitHub and retry the same checked commit.");store.Complete(job.Id);continue;}
                // A queued control action supersedes an interrupted command, but
                // failure OF the control action must not leave a permanent pending state.
                var superseded=(job.Kind is "start" or "resume" or "browse" && run.State is "cancelling" or "pausing")
                    || (job.Kind is "suspend" or "close-workspace" && run.State=="cancelling");
                if(!superseded)
                {
                    logger.LogWarning(ex,"Run operation failed: {RunId}",run.Id);
                    string phase="Unknown";
                    try{phase=await Suspend(run,stoppingToken);}catch(Exception releaseError){logger.LogWarning(releaseError,"Could not confirm release for {RunId}",run.Id);}
                    var recovery=phase=="Unknown" || job.Kind is "cancel" or "finish" or "suspend" or "close-workspace";
                    store.Update(run.Id,r=>r with{State=recovery?"recovery-required":"failed",PlatformPhase=phase,Error=$"{job.Kind} failed. Check the durable event log; no successful completion was inferred."},
                        recovery?"Operation failed; the requested platform state is unconfirmed. Retry suspend or cancel before further work.":"Execution failed; AX confirmed the environment is not running.");
                }
                store.Complete(job.Id);
            }
            finally{_active.TryRemove(job.RunId,out _);}
        }
    }
    private async Task Execute(WorkItem job,CancellationToken turnToken,CancellationToken hostToken)
    {
        var run=store.Get(job.RunId)!;
        if(job.Kind is "start" or "resume")
        {
            if(run.State is "cancelling" or "pausing")return;
            SetProgress(run.Id,job.Kind=="start"?"preparing":"resuming","Preparing the approved AX environment.");
            await platform.EnsureAsync(run,turnToken);
            var phase=await platform.ResumeAsync(run,turnToken);
            await repositories.Prepare(run,turnToken);
            turnToken.ThrowIfCancellationRequested();
            store.Update(run.Id,r=>r.State is "cancelling" or "pausing"?r:r with{State="running",PlatformPhase=phase,Turn=r.Turn+1,ProcessId=null,Publication=r.Publication is null?null:r.Publication with{State="not-ready",Head=null,Diff="",Checks="",Error=null}},run.Template=="codex-task"?"AX confirmed Running; starting Codex inside the sandbox.":"AX confirmed Running; starting the sandbox command (not an AI turn).");
            run=store.Get(run.Id)!;turnToken.ThrowIfCancellationRequested();
            if(run.State is "cancelling" or "pausing")return;
            var exit=await platform.ExecuteAsync(run,job.Message,
                pid=>store.Update(run.Id,r=>r with{ProcessId=pid}),
                (kind,text)=>store.Append(run.Id,kind,text),turnToken);
            store.Append(run.Id,"exit",$"Sandbox process exited with code {exit}.");
            GitPublication? review=store.Get(run.Id)!.Publication;
            if(exit==0&&run.Repository is not null){
                store.Append(run.Id,"system","Checking the committed repository before publication.");
                try{review=await repositories.Capture(store.Get(run.Id)!,turnToken);}
                catch(Exception ex)when(ex is not OperationCanceledException){review=(review??new()) with{State="blocked",Head=null,CheckedTurn=run.Turn,Error=ex is PortalException?ex.Message:"Repository checks or bundle validation failed. Nothing was published."};}
            }
            var suspended=await Suspend(run,hostToken);
            store.FinishTurn(run.Id,exit,suspended,review);
        }
        else if(job.Kind=="publish")
        {
            if(run.State!="publishing"||run.Publication?.Head!=job.Message)throw new PortalException(409,"Publication no longer matches this checked commit.");
            await repositories.Publish(run,turnToken);
            store.Update(run.Id,r=>r with{State="review",Error=null});
        }
        else if(job.Kind=="browse")
        {
            if(run.State!="opening-workspace")return;
            var phase=await platform.ResumeAsync(run,turnToken);turnToken.ThrowIfCancellationRequested();
            store.Update(run.Id,r=>r.State!="opening-workspace"?r:r with{State="browsing",PlatformPhase=phase,ProcessId=null,BrowseExpiresAt=DateTimeOffset.UtcNow.AddSeconds(options.BrowseIdleSeconds)},"Workspace opened for read-only browsing. No agent turn or model request started; close it when done.");
            while(true)
            {
                await Task.Delay(250,turnToken);
                using var held=await locks.Enter(run.Id,turnToken);
                var latest=store.Get(run.Id)!;
                if(latest.State!="browsing")return;
                if(latest.BrowseExpiresAt>DateTimeOffset.UtcNow&&!store.HasWaitingWork())continue;
                store.Update(run.Id,r=>r with{State="closing-workspace",BrowseExpiresAt=null});
                break;
            }
            var suspended=await Suspend(run,hostToken);
            store.Update(run.Id,r=>r.State!="closing-workspace"?r:r with{State=job.Message is "review" or "failed" or "suspended"?job.Message:"suspended",PlatformPhase=suspended,BrowseExpiresAt=null},"Workspace browsing ended (idle limit or queued work); AX confirmed suspension. No agent turn was started.");
        }
        else if(job.Kind is "suspend" or "close-workspace")
        {
            var phase=await Suspend(run,hostToken);
            store.Update(run.Id,r=>r with{State="suspended",PlatformPhase=phase,BrowseExpiresAt=null},"AX confirmed the environment is not running. Resume when ready.");
        }
        else
        {
            await Delete(run,hostToken);repositories.Cleanup(run);
            store.Update(run.Id,r=>r with{State=job.Kind=="cancel"?"cancelled":"completed",PlatformPhase="Deleted"},"AX task and workspace definitions deleted. Authorized portal history is retained; snapshot garbage collection follows platform policy.");
        }
    }
    private void PublicationFailed(Run run,string message)=>store.Update(run.Id,r=>r with{State="review",Publication=r.Publication! with{State="failed",Error=message}},message);
    private async Task<string> Suspend(Run run,CancellationToken ct)
    {using var held=await locks.Enter(run.Id,ct);store.Update(run.Id,r=>r with{PlatformPhase="Suspending"});var phase=await platform.SuspendAsync(run,ct);store.Update(run.Id,r=>r with{PlatformPhase=phase});return phase;}
    private async Task Delete(Run run,CancellationToken ct)
    {using var held=await locks.Enter(run.Id,ct);store.Update(run.Id,r=>r with{PlatformPhase="Deleting"});await platform.DeleteAsync(run,ct);store.Update(run.Id,r=>r with{PlatformPhase="Deleted"});}
    private void SetProgress(string id,string state,string message)=>store.Update(id,r=>r.State is "cancelling" or "pausing"?r:r with{State=state},message);
}
