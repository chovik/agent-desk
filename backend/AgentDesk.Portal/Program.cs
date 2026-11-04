using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentDesk.Portal;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Microsoft.Extensions.FileProviders;

if(args.Length==1 && args[0]=="--hash-password")
{
    // For the local provisioning helper; plaintext arrives via stdin, never argv/logs.
    var password=Console.ReadLine() ?? throw new InvalidOperationException("Password missing on stdin.");
    if(password.Length<16)throw new InvalidOperationException("Use at least 16 characters.");
    Console.WriteLine(new PasswordHasher<string>().HashPassword("lab",password));return;
}
var builder=WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning); // Callback codes are not request-log material.
var options=builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new();
if(string.IsNullOrWhiteSpace(options.DataDirectory) || string.IsNullOrWhiteSpace(options.UsersFile) ||
   !options.RunnerImage.Contains("@sha256:",StringComparison.Ordinal))
    throw new InvalidOperationException("Configure Portal DataDirectory, UsersFile and a digest-pinned RunnerImage. No anonymous/default-credential mode is provided.");
if(!Uri.TryCreate(options.RouterAddress,UriKind.Absolute,out var router) || router.Scheme!="https")
    throw new InvalidOperationException("The guest router requires verified HTTPS.");
Uri? publicOrigin=null;
if(!string.IsNullOrEmpty(options.PublicOrigin))
{
    if(!Uri.TryCreate(options.PublicOrigin,UriKind.Absolute,out publicOrigin) || publicOrigin.Scheme!="https" ||
       publicOrigin.AbsolutePath!="/" || publicOrigin.Query!="" || publicOrigin.Fragment!="" || publicOrigin.UserInfo!="" || options.AllowInsecureLabCookie)
        throw new InvalidOperationException("PublicOrigin must be an HTTPS origin with secure-only cookies.");
    builder.Services.Configure<ForwardedHeadersOptions>(o=>
    {
        // Trust only loopback plus explicitly configured ingress source networks.
        // NetworkPolicy must also admit only the intended ingress controller.
        o.ForwardedHeaders=ForwardedHeaders.XForwardedProto;o.ForwardLimit=1;
        o.KnownIPNetworks.Clear();o.KnownProxies.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);o.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach(var network in ProxyTrust.Parse(options.TrustedProxyNetworks))o.KnownIPNetworks.Add(network);
    });
}
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LabIdentity>();builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<ArtifactStore>();builder.Services.AddSingleton<RunLocks>();builder.Services.AddSingleton<WorkspaceAccess>();
if(options.BrowseIdleSeconds is <1 or >600)throw new InvalidOperationException("Browse idle timeout must be 1–600 seconds.");
builder.Services.AddSingleton<GithubVault>();builder.Services.AddSingleton<IGithubApi,GithubApi>();builder.Services.AddSingleton<GithubConnections>();
builder.Services.AddSingleton<CodexVault>();builder.Services.AddSingleton<CodexConnections>();
builder.Services.AddSingleton<IExecutionPlatform,AxExecutionPlatform>();
builder.Services.AddSingleton<ITrustedGit,TrustedGit>();builder.Services.AddSingleton<IRepositoryWorkflow,RepositoryWorkflow>();
builder.Services.AddSingleton<RunWorker>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<RunWorker>());
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory,"keys"))).SetApplicationName("AgentDesk");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>
{
    o.Cookie.Name="agentdesk.session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;
    o.Cookie.SecurePolicy=options.AllowInsecureLabCookie?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
    o.ExpireTimeSpan=TimeSpan.FromHours(8);o.SlidingExpiration=false;
    o.Events.OnRedirectToLogin=ctx=>{ctx.Response.StatusCode=401;return Task.CompletedTask;};
    o.Events.OnRedirectToAccessDenied=ctx=>{ctx.Response.StatusCode=403;return Task.CompletedTask;};
    o.Events.OnValidatePrincipal=ctx=>
    {
        var id=ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if(id is null || !ctx.HttpContext.RequestServices.GetRequiredService<LabIdentity>().Exists(id))ctx.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="agentdesk.csrf";o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=options.AllowInsecureLabCookie?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
builder.Services.AddRateLimiter(o=>
{
    o.RejectionStatusCode=429;
    // Bound buffered live reads/exports across all users until HTTP responses finish.
    o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(ctx=>ctx.Request.Path.Value?.Contains("/workspace",StringComparison.Ordinal)==true
        ?RateLimitPartition.GetConcurrencyLimiter("workspace-all",_=>new(){PermitLimit=2,QueueLimit=0})
        :RateLimitPartition.GetNoLimiter("other"));
    o.AddPolicy("workspace",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)??"anonymous",_=>new(){PermitLimit=120,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("codex-login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)??"anonymous",_=>new(){PermitLimit=3,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new(){PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=32768);
var app=builder.Build();
_ = app.Services.GetRequiredService<CodexVault>();
_ = app.Services.GetRequiredService<GithubVault>();
_ = app.Services.GetRequiredService<LabIdentity>(); // Fail closed on invalid account configuration.
if(publicOrigin is not null)app.UseForwardedHeaders();
app.Use(async(ctx,next)=>
{
    if(publicOrigin is not null && ctx.Request.Path!="/healthz")
    {
        if(!ctx.Request.IsHttps || !string.Equals(ctx.Request.Host.Host,publicOrigin.Host,StringComparison.OrdinalIgnoreCase) || (ctx.Request.Host.Port??443)!=publicOrigin.Port)
        {ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="Use the configured public HTTPS origin."});return;}
        if(HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Headers.TryGetValue("Origin",out var origin) && origin.ToString()!=publicOrigin.GetLeftPart(UriPartial.Authority))
        {ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="Untrusted request origin."});return;}
        ctx.Response.Headers["Strict-Transport-Security"]="max-age=86400";
    }
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    ctx.Response.Headers["Referrer-Policy"]="no-referrer";
    // The historical demo uses inline style attributes, but never inline scripts.
    // Keep the connected portal's style policy strict.
    var demo=ctx.Request.Path=="/demo" || ctx.Request.Path=="/index.html";
    ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; style-src-attr "+(demo?"'unsafe-inline'":"'none'")+"; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self' https://github.com";
    if(ctx.Request.Path.StartsWithSegments("/api"))ctx.Response.Headers.CacheControl="no-store";
    try{await next();}
    catch(PortalException ex)when(!ctx.Response.HasStarted){ctx.Response.StatusCode=ex.Status;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}
    catch(AntiforgeryValidationException)when(!ctx.Response.HasStarted){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="Invalid CSRF token. Reload and retry."});}
});
app.UseRouting();app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
app.Use(async(ctx,next)=>
{
    if(ctx.Request.Path.StartsWithSegments("/api") && HttpMethods.IsPost(ctx.Request.Method))
        await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
    await next();
});
var staticPath=Path.GetFullPath(options.StaticDirectory);
if(Directory.Exists(staticPath))app.UseStaticFiles(new StaticFileOptions{FileProvider=new PhysicalFileProvider(staticPath)});
app.MapGet("/",()=>Results.File(Path.Combine(staticPath,"live.html"),"text/html"));
app.MapGet("/demo",()=>Results.File(Path.Combine(staticPath,"index.html"),"text/html"));
app.MapGet("/github/setup/callback",async Task<IResult>(string state,string code,HttpContext ctx,GithubConnections github)=>
{
    ctx.Response.Headers.CacheControl="no-store";
    try{await github.Complete(state,code,ctx.RequestAborted);return Results.Redirect("/#/connections");}
    catch(Exception ex)when(ex is PortalException or IOException or System.Security.Cryptography.CryptographicException)
    {
        return Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>GitHub setup</title><h1>GitHub setup did not complete</h1><p>The setup link expired or GitHub could not be reached. For security, each link can only be used once.</p><p><a href=\"/#/connections\">Return to Connections</a> and start Connect GitHub again. Do not reuse this callback URL.</p></html>","text/html",statusCode:ex is PortalException p?p.Status:502);
    }
});
app.MapGet("/healthz",()=>Results.Ok(new{status="ok",mode="private-lab"}));
app.MapGet("/api/session",(HttpContext ctx,IAntiforgery csrf)=>Results.Ok(new
{
    authenticated=ctx.User.Identity?.IsAuthenticated==true,
    username=ctx.User.Identity?.Name,
    csrfToken=csrf.GetAndStoreTokens(ctx).RequestToken,
    mode="real-ax-lab",
    aiExecutionEnabled=options.CodexEnabled&&options.CodexRunnerImage.Contains("@sha256:")
}));
app.MapPost("/api/login",async(LoginRequest input,HttpContext ctx,LabIdentity identity)=>
{
    var user=identity.Authenticate(input.Username,input.Password);
    if(user is null)return Results.Json(new{error="Invalid username or password."},statusCode:401);
    var principal=new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,user.Id),new(ClaimTypes.Name,user.Username)],CookieAuthenticationDefaults.AuthenticationScheme));
    await ctx.SignInAsync(principal,new AuthenticationProperties{IsPersistent=false});return Results.Ok(new{authenticated=true});
}).RequireRateLimiting("login");
app.MapPost("/api/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync();return Results.Ok();}).RequireAuthorization();
static string Owner(HttpContext ctx)=>ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
static string Key(HttpContext ctx)=>ctx.Request.Headers["Idempotency-Key"].ToString();
var api=app.MapGroup("/api").RequireAuthorization();
api.MapGet("/connections/github",(HttpContext ctx,GithubConnections github)=>Results.Ok(github.View(Owner(ctx))));
api.MapPost("/connections/github/setup",(HttpContext ctx,GithubConnections github)=>Results.Ok(github.Start(Owner(ctx)))).RequireRateLimiting("codex-login");
api.MapGet("/connections/github/installations",async(HttpContext ctx,GithubConnections github)=>Results.Ok(await github.Installations(Owner(ctx),ctx.RequestAborted)));
api.MapPost("/connections/github/installation",async(GithubInstallationRequest input,HttpContext ctx,GithubConnections github)=>Results.Ok(await github.Select(Owner(ctx),input.Id,ctx.RequestAborted,input.AllowAllRepositories)));
api.MapPost("/connections/github/disconnect",async(HttpContext ctx,GithubConnections github)=>{await github.Disconnect(Owner(ctx),ctx.RequestAborted);return Results.Ok(github.View(Owner(ctx)));});
api.MapGet("/repositories",async(HttpContext ctx,GithubConnections github)=>Results.Ok(await github.Repositories(Owner(ctx),ctx.RequestAborted)));
api.MapGet("/connections/codex",(HttpContext ctx,CodexConnections connections)=>Results.Ok(connections.View(Owner(ctx))));
api.MapPost("/connections/codex/login",async(HttpContext ctx,CodexConnections connections)=>Results.Ok(await connections.Start(Owner(ctx),ctx.RequestAborted))).RequireRateLimiting("codex-login");
api.MapPost("/connections/codex/cancel",async(HttpContext ctx,CodexConnections connections)=>Results.Ok(await connections.Cancel(Owner(ctx),false,ctx.RequestAborted)));
api.MapPost("/connections/codex/disconnect",async(HttpContext ctx,CodexConnections connections)=>Results.Ok(await connections.Cancel(Owner(ctx),true,ctx.RequestAborted)));
api.MapGet("/catalog",(HttpContext ctx,CodexConnections connectionStore)=>Results.Ok(new
{
    templates=new[]{new{id="environment-check",name="Check environment & saved state",description="Runs a real Python command; no AI or external repository."},new{id="failure-check",name="Verify failure reporting",description="Runs a real command that intentionally exits with code 7."},new{id="codex-task",name="Codex · create or edit workspace files",description="Real Codex messages and commands in AX. Requires your ChatGPT connection. Five-minute / eight-model-request limit; commands have no internet access."}}.Where(t=>t.id!="codex-task"||options.CodexEnabled&&options.CodexRunnerImage.Contains("@sha256:")),
    githubEnabled=options.GithubEnabled,
    workspace=new{id="lab",name="Private sandbox workspace"},
    environment=new{name="AX / gVisor sandbox",cpu="1",memory="512 MiB checks · 1 GiB Codex"},
    connections=new[]{new{id="personal-codex",name="My ChatGPT / Codex",state=connectionStore.View(Owner(ctx)).State,description="Owner-bound account broker. Provider tokens remain outside the sandbox."}}
}));
api.MapGet("/runs",(HttpContext ctx,RunStore store)=>Results.Ok(store.List(Owner(ctx))));
api.MapPost("/runs",async(CreateRunRequest input,HttpContext ctx,RunStore store,CodexConnections connections,GithubConnections github)=>
{
    Catalog.Validate(input);
    string? connection=null;
    if(input.Template=="codex-task")
    {
        if(!options.CodexEnabled||!options.CodexRunnerImage.Contains("@sha256:"))throw new PortalException(409,"Codex execution is not deployed.");
        connection=connections.CurrentConnection(Owner(ctx));
    }
    return Results.Json(store.Create(Owner(ctx),input,Key(ctx),connection,input.Repository is null?null:await github.Bind(Owner(ctx),input.Repository,ctx.RequestAborted)),statusCode:202);
});
api.MapGet("/runs/{id}",(string id,HttpContext ctx,RunStore store)=>Results.Ok(store.GetOwned(Owner(ctx),id)));
api.MapGet("/runs/{id}/files",(string id,HttpContext ctx,RunStore store,ArtifactStore files)=>Results.Ok(new{snapshot=files.List(store.GetOwned(Owner(ctx),id))}));
api.MapGet("/runs/{id}/files/content",(string id,string path,int turn,HttpContext ctx,RunStore store,ArtifactStore files)=>
{
    var bytes=files.Read(store.GetOwned(Owner(ctx),id),path,turn);string? text=null;
    try{text=new System.Text.UTF8Encoding(false,true).GetString(bytes);if(text.Contains('\0'))text=null;}catch(System.Text.DecoderFallbackException){}
    return Results.Ok(new{path,size=bytes.Length,isBinary=text is null,truncated=text?.Length>200000,text=text is null?null:text[..Math.Min(text.Length,200000)]});
});
api.MapGet("/runs/{id}/files/download",(string id,string path,int turn,HttpContext ctx,RunStore store,ArtifactStore files)=>
    Results.File(files.Read(store.GetOwned(Owner(ctx),id),path,turn),"application/octet-stream",Path.GetFileName(path)));
api.MapGet("/runs/{id}/workspace",async(string id,string? path,HttpContext ctx,WorkspaceAccess workspace)=>
    Results.Ok(await workspace.List(Owner(ctx),id,path??"",ctx.RequestAborted))).RequireRateLimiting("workspace");
api.MapGet("/runs/{id}/workspace/content",async(string id,string path,HttpContext ctx,WorkspaceAccess workspace)=>
{
    var bytes=await workspace.Read(Owner(ctx),id,path,ctx.RequestAborted);string? text=null;
    try{text=new System.Text.UTF8Encoding(false,true).GetString(bytes);if(text.Contains('\0'))text=null;}catch(System.Text.DecoderFallbackException){}
    return Results.Ok(new{path,size=bytes.Length,isBinary=text is null,truncated=text?.Length>200000,text=text is null?null:text[..Math.Min(text.Length,200000)]});
}).RequireRateLimiting("workspace");
api.MapGet("/runs/{id}/workspace/download",async(string id,string path,HttpContext ctx,WorkspaceAccess workspace)=>
    Results.File(await workspace.Read(Owner(ctx),id,path,ctx.RequestAborted),"application/octet-stream",Path.GetFileName(path))).RequireRateLimiting("workspace");
api.MapPost("/runs/{id}/workspace/export",async(string id,HttpContext ctx,WorkspaceAccess workspace)=>
    Results.Ok(new{snapshot=await workspace.Export(Owner(ctx),id,ctx.RequestAborted)})).RequireRateLimiting("workspace");
foreach(var action in new[]{"resume","suspend","finish","cancel","browse","close-workspace","publish"})
{
    var verb=action;
    api.MapPost("/runs/{id}/"+verb,(string id,ActionRequest input,HttpContext ctx,RunStore store,RunWorker worker,CodexConnections connections)=>
    {
        var current=store.GetOwned(Owner(ctx),id);
        if(verb=="resume"&&current.Template=="codex-task")connections.CurrentConnection(current.Owner,current.ConnectionId);
        var run=store.QueueAction(Owner(ctx),id,verb,input.Message,Key(ctx));
        if(verb is "cancel" or "suspend" or "close-workspace")worker.Interrupt(id);
        return Results.Json(run,statusCode:202);
    });
}
api.MapGet("/runs/{id}/events",async(string id,HttpContext ctx,RunStore store)=>
{
    var owner=Owner(ctx);store.GetOwned(owner,id);
    var raw=ctx.Request.Headers["Last-Event-ID"].FirstOrDefault()??ctx.Request.Query["after"].FirstOrDefault()??"0";
    if(!long.TryParse(raw,out var after)||after<0)throw new PortalException(400,"Invalid event cursor.");
    ctx.Response.ContentType="text/event-stream";ctx.Response.Headers["X-Accel-Buffering"]="no";
    using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);lifetime.CancelAfter(TimeSpan.FromSeconds(55));
    try
    {
        while(!lifetime.IsCancellationRequested)
        {
            var events=store.Events(owner,id,after);
            foreach(var e in events)
            {
                await ctx.Response.WriteAsync($"id: {e.Id}\ndata: {JsonSerializer.Serialize(e,new JsonSerializerOptions(JsonSerializerDefaults.Web))}\n\n",lifetime.Token);after=e.Id;
            }
            await ctx.Response.WriteAsync(": heartbeat\n\n",lifetime.Token);await ctx.Response.Body.FlushAsync(lifetime.Token);
            await Task.Delay(events.Count==200?1:750,lifetime.Token);
        }
    }
    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
});
app.Run();
public partial class Program { }
