using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;

namespace YobaLog.Core.Storage.Sqlite;

// Single owner of the connection-string format and per-store DB file naming. Every store
// previously held its own `Open()`/`PathFor(ws)` pair built from `SqliteLogStoreOptions` —
// duplicated across 10 stores. The factory centralizes that so a future change (e.g. shared
// pooling, a custom `Cache=` setting, switching providers) lands in one place.
//
// File layout (decision-log 2026-04-21 + Phase H schema choice):
//   {DataDirectory}/$system.meta.db      — admin tables (Workspaces / Users / AdminTokens /
//                                          RetentionPolicies) + per-workspace meta of $system.
//   {DataDirectory}/{ws}.meta.db         — per-workspace meta (ApiKeys / SavedQueries /
//                                          MaskingPolicies / ShareLinks).
//   {DataDirectory}/{ws}.logs.db         — events table for that workspace.
//   {DataDirectory}/{ws}.traces.db       — spans table for that workspace.
//
// "Admin" and "WorkspaceMeta($system)" both resolve to the same file — `$system` IS a
// workspace, and admin tables share that file with the per-workspace meta tables. The split
// methods keep callers' intent visible at the call site (admin store vs. per-workspace store).
public sealed class SqliteConnectionFactory
{
    readonly SqliteLogStoreOptions _options;

    public SqliteConnectionFactory(IOptions<SqliteLogStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public string DataDirectory => _options.DataDirectory;

    public string AdminMetaPath => Path.Combine(_options.DataDirectory, $"{WorkspaceId.System.Value}.meta.db");
    public string WorkspaceMetaPath(WorkspaceId ws) => Path.Combine(_options.DataDirectory, $"{ws.Value}.meta.db");
    public string WorkspaceLogsPath(WorkspaceId ws) => Path.Combine(_options.DataDirectory, $"{ws.Value}.logs.db");
    public string WorkspaceTracesPath(WorkspaceId ws) => Path.Combine(_options.DataDirectory, $"{ws.Value}.traces.db");

    public DataConnection OpenAdmin() => Open(AdminMetaPath);
    public DataConnection OpenWorkspaceMeta(WorkspaceId ws) => Open(WorkspaceMetaPath(ws));
    public DataConnection OpenWorkspaceLogs(WorkspaceId ws) => Open(WorkspaceLogsPath(ws));
    public DataConnection OpenWorkspaceTraces(WorkspaceId ws) => Open(WorkspaceTracesPath(ws));

    public void EnsureDataDirectory() => Directory.CreateDirectory(_options.DataDirectory);

    static DataConnection Open(string path) =>
        SQLiteTools.CreateDataConnection($"Data Source={path};Cache=Shared");
}
