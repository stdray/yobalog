using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using YobaLog.Core.Auth;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing;

namespace YobaLog.Core.Admin.Sqlite;

public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    readonly SqliteConnectionFactory _connections;
    readonly ILogStore _logStore;
    readonly ISpanStore _spans;
    readonly IApiKeyAdmin _apiKeyAdmin;
    readonly ISavedQueryStore _savedQueries;
    readonly IFieldMaskingPolicyStore _maskingPolicies;
    readonly IShareLinkStore _shareLinks;

    public SqliteWorkspaceStore(
        SqliteConnectionFactory connections,
        ILogStore logStore,
        ISpanStore spans,
        IApiKeyAdmin apiKeyAdmin,
        ISavedQueryStore savedQueries,
        IFieldMaskingPolicyStore maskingPolicies,
        IShareLinkStore shareLinks)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
        _logStore = logStore;
        _spans = spans;
        _apiKeyAdmin = apiKeyAdmin;
        _savedQueries = savedQueries;
        _maskingPolicies = maskingPolicies;
        _shareLinks = shareLinks;
    }

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        _connections.EnsureDataDirectory();

        await using var db = _connections.OpenAdmin();
        await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        foreach (var stmt in SqliteAdminSchema.AllStatements)
            await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);

        await MigrateWorkspaceMetadataAsync(ct).ConfigureAwait(false);
    }

    async ValueTask MigrateWorkspaceMetadataAsync(CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        foreach (var (column, sql) in SqliteAdminSchema.MigrateWorkspaceMetadataMap)
        {
            var existingCount = db.Query<int>(
                "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $col",
                new DataParameter("$table", "Workspaces"),
                new DataParameter("$col", column));
            if (existingCount.FirstOrDefault() == 0)
                await db.ExecuteAsync(sql, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyList<WorkspaceInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var rows = await db.GetTable<WorkspaceRecord>()
            .OrderBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    public async ValueTask<WorkspaceInfo?> GetAsync(WorkspaceId id, CancellationToken ct)
    {
        await using var db = _connections.OpenAdmin();
        var idValue = id.Value;
        var row = await db.GetTable<WorkspaceRecord>().FirstOrDefaultAsync(r => r.Id == idValue, ct).ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async ValueTask<WorkspaceInfo> CreateAsync(
        WorkspaceId id,
        string description = "",
        string agent = "",
        string groupName = "",
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = _connections.OpenAdmin())
        {
            await db.InsertAsync(new WorkspaceRecord
            {
                Id = id.Value,
                CreatedAtMs = now.ToUnixTimeMilliseconds(),
                Description = description ?? "",
                Agent = agent ?? "",
                GroupName = groupName ?? "",
            }, token: ct).ConfigureAwait(false);
        }

        await _logStore.CreateWorkspaceAsync(id, new WorkspaceSchema(), ct).ConfigureAwait(false);
        await _spans.CreateWorkspaceAsync(id, ct).ConfigureAwait(false);
        await _savedQueries.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
        await _maskingPolicies.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
        await _shareLinks.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
        await _apiKeyAdmin.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
        return new WorkspaceInfo(id, now, description ?? "", agent ?? "", groupName ?? "");
    }

    public async ValueTask<WorkspaceInfo> GetOrCreateAsync(
        WorkspaceId id,
        string description,
        string agent,
        string groupName,
        CancellationToken ct)
    {
        var existing = await GetAsync(id, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        try
        {
            return await CreateAsync(id, description, agent, groupName, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return (await GetAsync(id, ct).ConfigureAwait(false))!;
        }
    }

    public async ValueTask<bool> DeleteAsync(WorkspaceId id, CancellationToken ct)
    {
        if (id == WorkspaceId.System)
            throw new InvalidOperationException("cannot delete the $system workspace");

        int deleted;
        await using (var db = _connections.OpenAdmin())
        {
            var idValue = id.Value;
            deleted = await db.GetTable<WorkspaceRecord>()
                .Where(r => r.Id == idValue)
                .DeleteAsync(ct)
                .ConfigureAwait(false);
        }

        if (deleted > 0)
        {
            await _logStore.DropWorkspaceAsync(id, ct).ConfigureAwait(false);
            await _spans.DropWorkspaceAsync(id, ct).ConfigureAwait(false);
            await _apiKeyAdmin.DropWorkspaceAsync(id, ct).ConfigureAwait(false);
        }

        return deleted > 0;
    }

    static WorkspaceInfo ToModel(WorkspaceRecord r) =>
        new(
            WorkspaceId.Parse(r.Id),
            DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs),
            r.Description ?? "",
            r.Agent ?? "",
            r.GroupName ?? "");
}
