namespace AgentDesk.Portal;

public sealed class PortalOptions
{
    public bool GithubEnabled { get; set; }
    public int BrowseIdleSeconds { get; set; } = 300;
    public string DataDirectory { get; set; } = "";
    public string UsersFile { get; set; } = "";
    public string StaticDirectory { get; set; } = "dist";
    public string AxAddress { get; set; } = "http://ax-server.ax-system.svc:8080";
    public string RouterAddress { get; set; } = "https://atenet-router.ate-system.svc:443";
    public string IdentityBundle { get; set; } = "/run/identity/bundle.pem";
    public string CaBundle { get; set; } = "/run/ca/bundle.pem";
    public string RunnerImage { get; set; } = "";
    public string CodexRunnerImage { get; set; } = "";
    public string CodexModel { get; set; } = "gpt-6.1-sol";
    public string Atespace { get; set; } = "agent-desk";
    public bool AllowInsecureLabCookie { get; set; }
    public bool CodexEnabled { get; set; }
    public string CodexBinary { get; set; } = "/usr/local/bin/codex";
    public string CodexScratch { get; set; } = "/run/codex";
    public string CodexVaultKey { get; set; } = "/run/codex-vault/key";
    public string CodexProxy { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string TrustedProxyNetworks { get; set; } = "";
}
public sealed record LabUser(string Id, string Username, string PasswordHash);
public sealed record LoginRequest(string Username, string Password);
public sealed record CreateRunRequest(string Title, string Instructions, string Template) { public GithubRepoRequest? Repository {get;init;} }
public sealed record ActionRequest(string Message = "");
public sealed record Run(string Id, string Owner, string Title, string Instructions, string Template,
    string State, string PlatformPhase, int Turn, string? ProcessId, string CreatedAt, string UpdatedAt, string? Error)
{
    public GithubBinding? Repository {get;init;}
    public bool RepositoryPrepared {get;init;}
    public GitPublication? Publication {get;init;}
    public string PublishBranch => "agent-desk/"+Id;
    public string? ConnectionId { get; init; }
    public string? ThreadId { get; init; }
    public DateTimeOffset? BrowseExpiresAt { get; init; }
    public string AxName => "portal-" + Id;
    public bool Terminal => State is "completed" or "cancelled";
}
public sealed record RunEvent(long Id, string RunId, string Kind, string Text, string CreatedAt);
public sealed record WorkItem(long Id, string RunId, string Kind, string Message);
public sealed class PortalException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
public static class Catalog
{
    public static readonly string[] Templates = ["environment-check", "failure-check", "codex-task"];
    public static void Validate(CreateRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 120)
            throw new PortalException(400, "Title must contain 1–120 characters.");
        if (string.IsNullOrWhiteSpace(request.Instructions) || request.Instructions.Length > 8000)
            throw new PortalException(400, "Instructions must contain 1–8000 characters.");
        if(request.Repository is not null && request.Template!="codex-task")throw new PortalException(400,"Only Codex tasks support a repository.");
        if (!Templates.Contains(request.Template)) throw new PortalException(400, "Unknown or unavailable template.");
    }
}
