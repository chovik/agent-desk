using System.Text.Json;
using Microsoft.AspNetCore.Identity;
namespace AgentDesk.Portal;

// Lab-only accounts: no registration endpoint, no provider tokens and no default password.
public sealed class LabIdentity
{
    private readonly LabUser[] _users;
    private readonly PasswordHasher<string> _hasher = new();
    private readonly string _dummyHash;
    public LabIdentity(PortalOptions options)
    {
        _users = JsonSerializer.Deserialize<LabUser[]>(File.ReadAllText(options.UsersFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (_users.Length == 0 || _users.Any(u => string.IsNullOrWhiteSpace(u.Id) ||
            string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.PasswordHash)) ||
            _users.Select(u => u.Id).Distinct().Count() != _users.Length ||
            _users.Select(u => u.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _users.Length)
            throw new InvalidOperationException("Invalid lab users file; refusing to start.");
        _dummyHash = _hasher.HashPassword("dummy", Guid.NewGuid().ToString());
    }
    public LabUser? Authenticate(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || username.Length > 100 || password.Length > 1024) return null;
        var user = _users.SingleOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        var result = _hasher.VerifyHashedPassword(user?.Id ?? "dummy", user?.PasswordHash ?? _dummyHash, password);
        return user is not null && result != PasswordVerificationResult.Failed ? user : null;
    }
    public bool Exists(string id) => _users.Any(u => u.Id == id);
}
