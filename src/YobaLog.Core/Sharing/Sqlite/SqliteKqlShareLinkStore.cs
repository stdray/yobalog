using LinqToDB;
using LinqToDB.Data;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Sharing.Sqlite;

public sealed class SqliteKqlShareLinkStore : IKqlShareLinkStore
{
    readonly SqliteConnectionFactory _connections;

    public SqliteKqlShareLinkStore(SqliteConnectionFactory connections)
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
        foreach (var stmt in SqliteKqlShareLinkSchema.AllStatements)
            await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
    }

    public async ValueTask<KqlShareLink> CreateAsync(
        WorkspaceId workspace,
        string kql,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var id = ShortGuid.NewShortGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        await using var db = _connections.OpenAdmin();
        await db.InsertAsync(new KqlShareLinkRecord
        {
            Id = id,
            Workspace = workspace.Value,
            Kql = kql,
            CreatedAtMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtMs = expiresAt.ToUnixTimeMilliseconds(),
        }, token: ct).ConfigureAwait(false);

        return new KqlShareLink(id, workspace, kql, now, expiresAt);
    }

    public async ValueTask<KqlShareLink?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var row = await db.GetTable<KqlShareLinkRecord>()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        return await db.GetTable<KqlShareLinkRecord>()
            .Where(r => r.Id == id)
            .DeleteAsync(ct)
            .ConfigureAwait(false) > 0;
    }

    public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var cutoff = now.ToUnixTimeMilliseconds();
        return await db.GetTable<KqlShareLinkRecord>()
            .Where(r => r.ExpiresAtMs < cutoff)
            .DeleteAsync(ct)
            .ConfigureAwait(false);
    }

    static KqlShareLink ToModel(KqlShareLinkRecord r) =>
        new(
            r.Id,
            WorkspaceId.Parse(r.Workspace),
            r.Kql,
            DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs),
            DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresAtMs));
}
