using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using YobaLog.Core.Admin;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Auth.Sqlite;

// DB-backed api-keys catalog. Each workspace owns its table in `<ws>.meta.db`, symmetric to
// saved queries / masking policies / share links. The hot path (ValidateAsync) hits an
// aggregated in-memory snapshot rebuilt on Initialize/Create/Delete/Drop — reads are lock-free.
//
// Plaintext tokens are never stored — only sha256(token) as hex + a 6-char prefix for UI
// identification. Plaintext is returned exactly once from CreateAsync.
//
// Wildcard keys (IsWildcard=1) are not scoped to a single workspace — they can authenticate
// to any workspace specified in ?workspace= on the request. CanCreate + CreateWindowHours
// control whether the key can lazily create new workspaces.
public sealed class SqliteApiKeyStore : IApiKeyStore, IApiKeyAdmin
{
    readonly SqliteConnectionFactory _connections;
    readonly Lock _sync = new();

    sealed record CacheEntry(
        WorkspaceId? Scope,
        bool IsWildcard,
        bool CanCreate,
        DateTimeOffset? CreateDeadline,
        string? Title);

    ImmutableDictionary<string, CacheEntry> _tokensByHash = ImmutableDictionary<string, CacheEntry>.Empty;
    ImmutableHashSet<WorkspaceId> _workspaces = ImmutableHashSet<WorkspaceId>.Empty;

    public SqliteApiKeyStore(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public IReadOnlyCollection<WorkspaceId> ConfiguredWorkspaces => _workspaces;

    public ValueTask<ApiKeyValidation> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return ValueTask.FromResult(ApiKeyValidation.Invalid("missing api key"));

        var hash = HashToken(token);
        if (!_tokensByHash.TryGetValue(hash, out var entry))
            return ValueTask.FromResult(ApiKeyValidation.Invalid("unknown api key"));

        if (!entry.IsWildcard)
            return ValueTask.FromResult(ApiKeyValidation.Valid(entry.Scope!.Value, entry.Title));

        return ValueTask.FromResult(ApiKeyValidation.Wildcard(entry.CanCreate, entry.CreateDeadline, entry.Title));
    }

    public async ValueTask InitializeWorkspaceAsync(WorkspaceId workspace, CancellationToken ct)
    {
        _connections.EnsureDataDirectory();

        await using (var db = _connections.OpenWorkspaceMeta(workspace))
        {
            await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
            await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
            foreach (var stmt in SqliteApiKeySchema.AllStatements)
                await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
        }

        // Migration: add wildcard columns for existing databases that predate this feature.
        await MigrateWildcardColumnsAsync(workspace, ct).ConfigureAwait(false);

        await MergeWorkspaceIntoCacheAsync(workspace, ct).ConfigureAwait(false);
    }

    async ValueTask MigrateWildcardColumnsAsync(WorkspaceId workspace, CancellationToken ct)
    {
        // Use PRAGMA table_info to check whether each column exists before running
        // ALTER TABLE ADD COLUMN — avoids exception-based control flow for idempotency.
        await using var db = _connections.OpenWorkspaceMeta(workspace);
        foreach (var (column, sql) in SqliteApiKeySchema.MigrateWildcardColumnMap)
        {
            var existingCount = db.Query<int>(
                "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $col",
                new DataParameter("$table", "ApiKeys"),
                new DataParameter("$col", column));
            if (existingCount.FirstOrDefault() == 0)
                await db.ExecuteAsync(sql, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyList<ApiKeyInfo>> ListAsync(WorkspaceId workspace, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspace);
        var rows = await db.GetTable<ApiKeyRecord>()
            .OrderBy(r => r.CreatedAtMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => ToModel(workspace, r)).ToList();
    }

    public async ValueTask<ApiKeyCreated> CreateAsync(
        WorkspaceId workspace,
        string? title,
        CancellationToken ct,
        bool isWildcard = false,
        bool canCreate = false,
        int createWindowHours = 0)
    {
        var plaintext = ShortGuid.NewShortGuid().ToString();
        var hash = HashToken(plaintext);
        var prefix = plaintext[..6];
        var id = ShortGuid.NewShortGuid().ToString();
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var now = DateTimeOffset.UtcNow;

        await using (var db = _connections.OpenWorkspaceMeta(workspace))
        {
            await db.InsertAsync(new ApiKeyRecord
            {
                Id = id,
                TokenHash = hash,
                Prefix = prefix,
                Title = normalizedTitle,
                CreatedAtMs = now.ToUnixTimeMilliseconds(),
                IsWildcard = isWildcard ? 1 : 0,
                CanCreate = canCreate ? 1 : 0,
                CreateWindowHours = createWindowHours,
            }, token: ct).ConfigureAwait(false);
        }

        var deadline = isWildcard && canCreate && createWindowHours > 0
            ? now.AddHours(createWindowHours)
            : (DateTimeOffset?)null;

        lock (_sync)
        {
            _tokensByHash = isWildcard
                ? _tokensByHash.SetItem(hash, new CacheEntry(null, true, canCreate, deadline, normalizedTitle))
                : _tokensByHash.SetItem(hash, new CacheEntry(workspace, false, false, null, normalizedTitle));
            _workspaces = _workspaces.Add(workspace);
        }

        return new ApiKeyCreated(
            new ApiKeyInfo(id, prefix, workspace, normalizedTitle, now),
            plaintext);
    }

    public async ValueTask<bool> DeleteAsync(WorkspaceId workspace, string id, CancellationToken ct)
    {
        string? hashToEvict = null;
        bool noKeysLeft;

        await using (var db = _connections.OpenWorkspaceMeta(workspace))
        {
            var row = await db.GetTable<ApiKeyRecord>()
                .FirstOrDefaultAsync(r => r.Id == id, ct)
                .ConfigureAwait(false);
            if (row is null)
                return false;

            hashToEvict = row.TokenHash;
            await db.GetTable<ApiKeyRecord>()
                .Where(r => r.Id == id)
                .DeleteAsync(ct)
                .ConfigureAwait(false);

            noKeysLeft = await db.GetTable<ApiKeyRecord>().CountAsync(ct).ConfigureAwait(false) == 0;
        }

        lock (_sync)
        {
            if (hashToEvict is not null)
                _tokensByHash = _tokensByHash.Remove(hashToEvict);
            if (noKeysLeft)
                _workspaces = _workspaces.Remove(workspace);
        }

        return true;
    }

    public async ValueTask DropWorkspaceAsync(WorkspaceId workspace, CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var path = _connections.WorkspaceMetaPath(workspace);
        if (File.Exists(path))
            File.Delete(path);
        foreach (var suffix in (ReadOnlySpan<string>)["-wal", "-shm", "-journal"])
        {
            var extra = path + suffix;
            if (File.Exists(extra))
                File.Delete(extra);
        }

        lock (_sync)
        {
            var tokens = _tokensByHash.ToBuilder();
            foreach (var (hash, entry) in _tokensByHash)
                if (entry.Scope == workspace)
                    tokens.Remove(hash);
            _tokensByHash = tokens.ToImmutable();
            _workspaces = _workspaces.Remove(workspace);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    async Task MergeWorkspaceIntoCacheAsync(WorkspaceId workspace, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspace);
        var rows = await db.GetTable<ApiKeyRecord>().ToListAsync(ct).ConfigureAwait(false);

        lock (_sync)
        {
            var tokens = _tokensByHash.ToBuilder();
            foreach (var (hash, entry) in _tokensByHash)
                if (entry.Scope == workspace)
                    tokens.Remove(hash);

            foreach (var r in rows)
            {
                var isWildcard = r.IsWildcard != 0;
                if (isWildcard)
                {
                    var deadlines = r.CanCreate != 0 && r.CreateWindowHours > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs).AddHours(r.CreateWindowHours)
                        : (DateTimeOffset?)null;
                    tokens[r.TokenHash] = new CacheEntry(null, true, r.CanCreate != 0, deadlines, r.Title);
                }
                else
                {
                    tokens[r.TokenHash] = new CacheEntry(workspace, false, false, null, r.Title);
                }
            }

            _tokensByHash = tokens.ToImmutable();
            _workspaces = rows.Count > 0 ? _workspaces.Add(workspace) : _workspaces.Remove(workspace);
        }
    }

    static ApiKeyInfo ToModel(WorkspaceId workspace, ApiKeyRecord r) =>
        new(r.Id, r.Prefix, workspace, r.Title, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs));

    static string HashToken(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        return Convert.ToHexStringLower(hash);
    }
}
