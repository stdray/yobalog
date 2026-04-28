using LinqToDB;
using LinqToDB.Data;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Retention.Sqlite;

public sealed class SqliteRetentionPolicyStore : IRetentionPolicyStore
{
    readonly SqliteConnectionFactory _connections;

    public SqliteRetentionPolicyStore(SqliteConnectionFactory connections)
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
        // Idempotent: Workspaces/Users/RetentionPolicies live in the same DB — SqliteWorkspaceStore
        // runs AllStatements on InitializeAsync, so this is usually redundant. Kept for standalone
        // use (tests, tooling).
        await db.ExecuteAsync(SqliteAdminSchema.CreateRetentionPolicies, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RetentionPolicy>> ListAsync(CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var rows = await db.GetTable<RetentionPolicyRecord>()
            .OrderBy(r => r.Workspace).ThenBy(r => r.SavedQuery)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    public async ValueTask<IReadOnlyList<RetentionPolicy>> ListByWorkspaceAsync(WorkspaceId workspace, CancellationToken ct)
    {
        var wsValue = workspace.Value;
        await using var db = _connections.OpenAdmin();
        var rows = await db.GetTable<RetentionPolicyRecord>()
            .Where(r => r.Workspace == wsValue)
            .OrderBy(r => r.SavedQuery)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    public async ValueTask UpsertAsync(RetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrEmpty(policy.Workspace)) throw new ArgumentException("workspace required", nameof(policy));
        if (string.IsNullOrEmpty(policy.SavedQuery)) throw new ArgumentException("saved query required", nameof(policy));
        if (policy.RetainDays <= 0) throw new ArgumentException("retain days must be positive", nameof(policy));

        await using var db = _connections.OpenAdmin();
        await db.InsertOrReplaceAsync(new RetentionPolicyRecord
        {
            Workspace = policy.Workspace,
            SavedQuery = policy.SavedQuery,
            RetainDays = policy.RetainDays,
        }, token: ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(WorkspaceId workspace, string savedQuery, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(savedQuery);

        var wsValue = workspace.Value;
        await using var db = _connections.OpenAdmin();
        var deleted = await db.GetTable<RetentionPolicyRecord>()
            .Where(r => r.Workspace == wsValue && r.SavedQuery == savedQuery)
            .DeleteAsync(ct)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    static RetentionPolicy ToModel(RetentionPolicyRecord r) =>
        new() { Workspace = r.Workspace, SavedQuery = r.SavedQuery, RetainDays = r.RetainDays };
}
