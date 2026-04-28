using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.SavedQueries.Sqlite;

public sealed class SqliteSavedQueryStore : ISavedQueryStore
{
    readonly SqliteConnectionFactory _connections;

    public SqliteSavedQueryStore(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public async ValueTask InitializeWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        _connections.EnsureDataDirectory();

        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        foreach (var stmt in SqliteSavedQuerySchema.AllStatements)
            await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
    }

    public async ValueTask<SavedQuery> UpsertAsync(WorkspaceId workspaceId, string name, string kql, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kql);

        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var table = db.GetTable<SavedQueryRecord>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var existing = await table.FirstOrDefaultAsync(q => q.Name == name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingId = existing.Id;
            var createdAtMs = existing.CreatedAtMs;
            await table
                .Where(q => q.Id == existingId)
                .Set(q => q.Kql, kql)
                .Set(q => q.UpdatedAtMs, now)
                .UpdateAsync(ct)
                .ConfigureAwait(false);
            return new SavedQuery(
                existingId,
                name,
                kql,
                DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs),
                DateTimeOffset.FromUnixTimeMilliseconds(now));
        }

        var newId = await db.InsertWithInt64IdentityAsync(new SavedQueryRecord
        {
            Name = name,
            Kql = kql,
            CreatedAtMs = now,
            UpdatedAtMs = now,
        }, token: ct).ConfigureAwait(false);

        return new SavedQuery(
            newId,
            name,
            kql,
            DateTimeOffset.FromUnixTimeMilliseconds(now),
            DateTimeOffset.FromUnixTimeMilliseconds(now));
    }

    public async ValueTask<SavedQuery?> GetAsync(WorkspaceId workspaceId, long id, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var row = await db.GetTable<SavedQueryRecord>()
            .FirstOrDefaultAsync(q => q.Id == id, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async ValueTask<SavedQuery?> GetByNameAsync(WorkspaceId workspaceId, string name, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var row = await db.GetTable<SavedQueryRecord>()
            .FirstOrDefaultAsync(q => q.Name == name, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async ValueTask<IReadOnlyList<SavedQuery>> ListAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var rows = await db.GetTable<SavedQueryRecord>()
            .OrderBy(q => q.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    public async ValueTask<bool> DeleteAsync(WorkspaceId workspaceId, long id, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var deleted = await db.GetTable<SavedQueryRecord>()
            .Where(q => q.Id == id)
            .DeleteAsync(ct)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    public async ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var path = _connections.WorkspaceMetaPath(workspaceId);
        if (File.Exists(path))
            File.Delete(path);
        foreach (var suffix in (ReadOnlySpan<string>)["-wal", "-shm", "-journal"])
        {
            var extra = path + suffix;
            if (File.Exists(extra))
                File.Delete(extra);
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    static SavedQuery ToModel(SavedQueryRecord r) => new(
        r.Id,
        r.Name,
        r.Kql,
        DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs),
        DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtMs));
}
