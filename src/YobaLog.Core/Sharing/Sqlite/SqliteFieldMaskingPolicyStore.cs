using System.Collections.Immutable;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Sharing.Sqlite;

public sealed class SqliteFieldMaskingPolicyStore : IFieldMaskingPolicyStore
{
    readonly SqliteConnectionFactory _connections;

    public SqliteFieldMaskingPolicyStore(SqliteConnectionFactory connections)
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
        foreach (var stmt in SqliteMaskingPolicySchema.AllStatements)
            await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
    }

    public async ValueTask<FieldMaskingPolicy> GetAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var rows = await db.GetTable<MaskingPolicyRecord>()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var builder = ImmutableDictionary.CreateBuilder<string, MaskMode>(StringComparer.Ordinal);
        foreach (var row in rows)
            builder[row.Path] = (MaskMode)row.Mode;
        return new FieldMaskingPolicy(builder.ToImmutable());
    }

    public async ValueTask UpsertAsync(WorkspaceId workspaceId, IReadOnlyDictionary<string, MaskMode> modes, CancellationToken ct)
    {
        if (modes.Count == 0)
            return;

        await using var db = _connections.OpenWorkspaceMeta(workspaceId);
        var table = db.GetTable<MaskingPolicyRecord>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var (path, mode) in modes)
        {
            var modeInt = (int)mode;
            var updated = await table
                .Where(r => r.Path == path)
                .Set(r => r.Mode, modeInt)
                .Set(r => r.UpdatedAtMs, now)
                .UpdateAsync(ct)
                .ConfigureAwait(false);
            if (updated == 0)
            {
                await db.InsertAsync(new MaskingPolicyRecord
                {
                    Path = path,
                    Mode = modeInt,
                    UpdatedAtMs = now,
                }, token: ct).ConfigureAwait(false);
            }
        }
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
}
