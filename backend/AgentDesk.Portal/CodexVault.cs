using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace AgentDesk.Portal;

public sealed record CodexCredential(string ConnectionId,string Owner,string AccountId,string? Email,string? Plan,string AuthJson,string UpdatedAt,bool RequiresReconnect=false);
public sealed class CodexVault
{
    private readonly byte[]? _key;private readonly string _database;private readonly object _gate=new();
    public bool Enabled=>_key is not null;
    public CodexVault(PortalOptions options)
    {
        _database=new SqliteConnectionStringBuilder{DataSource=Path.Combine(options.DataDirectory,"codex-vault.db"),Pooling=false}.ToString();
        if(!options.CodexEnabled)return;
        _key=File.ReadAllBytes(options.CodexVaultKey);if(_key.Length!=32)throw new InvalidOperationException("Codex vault requires a 32-byte external key.");
        Directory.CreateDirectory(options.DataDirectory);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS credentials(owner TEXT PRIMARY KEY, ciphertext BLOB NOT NULL)";cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var c=new SqliteConnection(_database);c.Open();return c;}
    private byte[] Protect(string owner,byte[] plain)
    {
        var nonce=RandomNumberGenerator.GetBytes(12);var cipher=new byte[plain.Length];var tag=new byte[16];
        using var aes=new AesGcm(_key!,16);aes.Encrypt(nonce,plain,cipher,tag,Encoding.UTF8.GetBytes(owner));return [..nonce,..tag,..cipher];
    }
    private byte[] Unprotect(string owner,byte[] blob)
    {var plain=new byte[blob.Length-28];using var aes=new AesGcm(_key!,16);aes.Decrypt(blob.AsSpan(0,12),blob.AsSpan(28),blob.AsSpan(12,16),plain,Encoding.UTF8.GetBytes(owner));return plain;}
    public CodexCredential? Get(string owner)
    {
        if(!Enabled)return null;lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT ciphertext FROM credentials WHERE owner=$owner";q.Parameters.AddWithValue("$owner",owner);if(q.ExecuteScalar() is not byte[] blob)return null;
            var plain=Unprotect(owner,blob);try{return JsonSerializer.Deserialize<CodexCredential>(plain);}finally{CryptographicOperations.ZeroMemory(plain);}}
    }
    public void Save(CodexCredential value)
    {
        if(!Enabled)throw new PortalException(503,"Codex connections are not enabled.");
        var plain=JsonSerializer.SerializeToUtf8Bytes(value);byte[] cipher;try{cipher=Protect(value.Owner,plain);}finally{CryptographicOperations.ZeroMemory(plain);}
        lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="INSERT INTO credentials VALUES($owner,$cipher) ON CONFLICT(owner) DO UPDATE SET ciphertext=excluded.ciphertext";q.Parameters.AddWithValue("$owner",value.Owner);q.Parameters.AddWithValue("$cipher",cipher);q.ExecuteNonQuery();}
    }
    public void Delete(string owner)
    {if(!Enabled)return;lock(_gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM credentials WHERE owner=$owner";q.Parameters.AddWithValue("$owner",owner);q.ExecuteNonQuery();}}
    // Only the officially managed ChatGPT cache is accepted, never an API key.
    public static string AccountId(string authJson)
    {
        using var doc=JsonDocument.Parse(authJson);var root=doc.RootElement;
        if(root.TryGetProperty("OPENAI_API_KEY",out var key)&&key.ValueKind!=JsonValueKind.Null)throw new PortalException(400,"Only a personal ChatGPT connection is supported.");
        var t=root.GetProperty("tokens");var id=t.GetProperty("account_id").GetString();
        if(string.IsNullOrWhiteSpace(id)||string.IsNullOrEmpty(t.GetProperty("access_token").GetString())||string.IsNullOrEmpty(t.GetProperty("refresh_token").GetString()))throw new PortalException(400,"Incomplete ChatGPT sign-in.");
        return id;
    }
}
