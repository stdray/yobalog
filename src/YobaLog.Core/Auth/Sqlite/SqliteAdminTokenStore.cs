using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Auth.Sqlite;

// SQLite-backed IAdminTokenStore + IAdminTokenAdmin. Lives in $system.meta.db alongside Users
// and Workspaces so cascade-on-user-delete can join the same transaction. Token shape mirrors
// SqliteApiKeyStore (sha256 hash, 6-char prefix, constant-time compare on validate); what
// differs:
//   - `Username` ties each token to a User (no SQL FK — handler-level integrity, same as the
//     yobaconf reference impl).
//   - No per-workspace scope — admin tokens carry full user-equivalent rights in MVP. Per-token
//     RBAC is deferred (see decision-log 2026-04-28).
public sealed class SqliteAdminTokenStore : IAdminTokenStore, IAdminTokenAdmin
{
    readonly SqliteConnectionFactory _connections;

    public SqliteAdminTokenStore(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        _connections.EnsureDataDirectory();

        await using var db = _connections.OpenAdmin();
        await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        // Idempotent — SqliteWorkspaceStore already runs AllStatements on the same DB. Kept
        // standalone for tests/tooling that bypass the workspace store.
        await db.ExecuteAsync(SqliteAdminSchema.CreateAdminTokens, ct).ConfigureAwait(false);
        await db.ExecuteAsync(SqliteAdminSchema.CreateAdminTokensUsernameIndex, ct).ConfigureAwait(false);
    }

    public async ValueTask<AdminTokenValidation> ValidateAsync(string? plaintextToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(plaintextToken))
            return new AdminTokenValidation.Invalid("missing admin-token");

        var hash = HashToken(plaintextToken);

        await using var db = _connections.OpenAdmin();
        var row = await db.GetTable<AdminTokenRecord>()
            .FirstOrDefaultAsync(r => r.TokenHash == hash && r.IsDeleted == 0, ct)
            .ConfigureAwait(false);
        if (row is null)
            return new AdminTokenValidation.Invalid("unknown admin-token");

        // The indexed lookup already implies a hash match; the constant-time check
        // double-protects against timing side-channels on the SQL path itself.
        var storedHashBytes = Convert.FromHexString(row.TokenHash);
        var candidateHashBytes = Convert.FromHexString(hash);
        if (!CryptographicOperations.FixedTimeEquals(storedHashBytes, candidateHashBytes))
            return new AdminTokenValidation.Invalid("unknown admin-token");

        return new AdminTokenValidation.Valid(ToDomain(row));
    }

    public async ValueTask<AdminTokenCreated> CreateAsync(string username, string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(description);

        var plaintext = ShortGuid.NewShortGuid().ToString();
        var hash = HashToken(plaintext);
        var prefix = plaintext[..6];
        var now = DateTimeOffset.UtcNow;

        await using var db = _connections.OpenAdmin();
        var id = Convert.ToInt64(await db.InsertWithIdentityAsync(new AdminTokenRecord
        {
            Username = username,
            TokenHash = hash,
            TokenPrefix = prefix,
            Description = description,
            UpdatedAtMs = now.ToUnixTimeMilliseconds(),
            IsDeleted = 0,
        }, token: ct).ConfigureAwait(false));

        return new AdminTokenCreated(
            new AdminTokenInfo(id, username, prefix, description, now),
            plaintext);
    }

    public async ValueTask<IReadOnlyList<AdminTokenInfo>> ListByUsernameAsync(string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var db = _connections.OpenAdmin();
        var rows = await db.GetTable<AdminTokenRecord>()
            .Where(r => r.Username == username && r.IsDeleted == 0)
            .OrderBy(r => r.Description)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToInfo).ToList();
    }

    public async ValueTask<bool> SoftDeleteAsync(long id, CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var updated = await db.GetTable<AdminTokenRecord>()
            .Where(r => r.Id == id && r.IsDeleted == 0)
            .Set(r => r.IsDeleted, 1)
            .Set(r => r.UpdatedAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .UpdateAsync(ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    public async ValueTask<int> HardDeleteByUsernameAsync(string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var db = _connections.OpenAdmin();
        return await db.GetTable<AdminTokenRecord>()
            .Where(r => r.Username == username)
            .DeleteAsync(ct)
            .ConfigureAwait(false);
    }

    static AdminToken ToDomain(AdminTokenRecord r) => new()
    {
        Id = r.Id,
        Username = r.Username,
        TokenPrefix = r.TokenPrefix,
        TokenHash = r.TokenHash,
        Description = r.Description,
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtMs),
        IsDeleted = r.IsDeleted != 0,
    };

    static AdminTokenInfo ToInfo(AdminTokenRecord r) =>
        new(r.Id, r.Username, r.TokenPrefix, r.Description, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtMs));

    static string HashToken(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        return Convert.ToHexStringLower(hash);
    }
}
