using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing;

namespace YobaLog.Core.Admin.Sqlite;

public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ILogStore _logStore;
	readonly ISpanStore _spans;
	readonly IApiKeyAdmin _apiKeyAdmin;
	readonly ISavedQueryStore _savedQueries;
	readonly IFieldMaskingPolicyStore _maskingPolicies;
	readonly IShareLinkStore _shareLinks;

	public SqliteWorkspaceStore(
		IOptions<SqliteLogStoreOptions> options,
		ILogStore logStore,
		ISpanStore spans,
		IApiKeyAdmin apiKeyAdmin,
		ISavedQueryStore savedQueries,
		IFieldMaskingPolicyStore maskingPolicies,
		IShareLinkStore shareLinks)
	{
		_options = options.Value;
		_logStore = logStore;
		_spans = spans;
		_apiKeyAdmin = apiKeyAdmin;
		_savedQueries = savedQueries;
		_maskingPolicies = maskingPolicies;
		_shareLinks = shareLinks;
	}

	string AdminDbPath => Path.Combine(_options.DataDirectory, $"{WorkspaceId.System.Value}.meta.db");

	DataConnection OpenAdmin() =>
		SQLiteTools.CreateDataConnection($"Data Source={AdminDbPath};Cache=Shared");

	public async ValueTask InitializeAsync(CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using var db = OpenAdmin();
		await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
		await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
		foreach (var stmt in SqliteAdminSchema.AllStatements)
			await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<WorkspaceInfo>> ListAsync(CancellationToken ct)
	{
		await using var db = OpenAdmin();
		var rows = new List<WorkspaceInfo>();
		await foreach (var row in db.GetTable<WorkspaceRecord>().OrderBy(r => r.Id).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			rows.Add(ToModel(row));
		return rows;
	}

	public async ValueTask<WorkspaceInfo?> GetAsync(WorkspaceId id, CancellationToken ct)
	{
		await using var db = OpenAdmin();
		var idValue = id.Value;
		var row = await db.GetTable<WorkspaceRecord>().FirstOrDefaultAsync(r => r.Id == idValue, ct).ConfigureAwait(false);
		return row is null ? null : ToModel(row);
	}

	public async ValueTask<WorkspaceInfo> CreateAsync(WorkspaceId id, CancellationToken ct)
	{
		var now = DateTimeOffset.UtcNow;
		await using (var db = OpenAdmin())
		{
			await db.InsertAsync(new WorkspaceRecord
			{
				Id = id.Value,
				CreatedAtMs = now.ToUnixTimeMilliseconds(),
			}, token: ct).ConfigureAwait(false);
		}

		await _logStore.CreateWorkspaceAsync(id, new WorkspaceSchema(), ct).ConfigureAwait(false);
		await _spans.CreateWorkspaceAsync(id, ct).ConfigureAwait(false);
		// All meta tables live in `<ws>.meta.db` — init all four stores so the first UI navigation
		// to the new workspace doesn't explode on a missing SavedQueries / Sharing / Masking table.
		// Mirrors WorkspaceBootstrapper.InitMetaAsync; the overlap is intentional since the
		// bootstrapper handles $system + restart-idempotency, and CreateAsync handles runtime creates.
		await _savedQueries.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
		await _maskingPolicies.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
		await _shareLinks.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
		await _apiKeyAdmin.InitializeWorkspaceAsync(id, ct).ConfigureAwait(false);
		return new WorkspaceInfo(id, now);
	}

	public async ValueTask<bool> DeleteAsync(WorkspaceId id, CancellationToken ct)
	{
		if (id == WorkspaceId.System)
			throw new InvalidOperationException("cannot delete the $system workspace");

		int deleted;
		await using (var db = OpenAdmin())
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
		new(WorkspaceId.Parse(r.Id), DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs));
}
