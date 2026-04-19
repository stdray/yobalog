using System.Collections.Concurrent;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.SavedQueries.Sqlite;

public sealed class SqliteSavedQueryStore : ISavedQueryStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();

	public SqliteSavedQueryStore(IOptions<SqliteLogStoreOptions> options)
	{
		_options = options.Value;
	}

	string PathFor(WorkspaceId ws) =>
		_pathCache.GetOrAdd(ws, w => Path.Combine(_options.DataDirectory, $"{w.Value}.meta.db"));

	DataConnection Open(WorkspaceId ws) =>
		SQLiteTools.CreateDataConnection($"Data Source={PathFor(ws)};Cache=Shared");

	public async ValueTask InitializeWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using var db = Open(workspaceId);
		await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
		await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
		foreach (var stmt in SqliteSavedQuerySchema.AllStatements)
			await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
	}

	public async ValueTask<SavedQuery> UpsertAsync(WorkspaceId workspaceId, string name, string kql, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(kql);

		await using var db = Open(workspaceId);
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
		await using var db = Open(workspaceId);
		var row = await db.GetTable<SavedQueryRecord>()
			.FirstOrDefaultAsync(q => q.Id == id, ct)
			.ConfigureAwait(false);
		return row is null ? null : ToModel(row);
	}

	public async ValueTask<SavedQuery?> GetByNameAsync(WorkspaceId workspaceId, string name, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var row = await db.GetTable<SavedQueryRecord>()
			.FirstOrDefaultAsync(q => q.Name == name, ct)
			.ConfigureAwait(false);
		return row is null ? null : ToModel(row);
	}

	public async ValueTask<IReadOnlyList<SavedQuery>> ListAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var rows = new List<SavedQuery>();
		await foreach (var r in db.GetTable<SavedQueryRecord>().OrderBy(q => q.Name).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			rows.Add(ToModel(r));
		return rows;
	}

	public async ValueTask<bool> DeleteAsync(WorkspaceId workspaceId, long id, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
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

		var path = PathFor(workspaceId);
		if (File.Exists(path))
			File.Delete(path);
		foreach (var suffix in (ReadOnlySpan<string>)["-wal", "-shm", "-journal"])
		{
			var extra = path + suffix;
			if (File.Exists(extra))
				File.Delete(extra);
		}
		_pathCache.TryRemove(workspaceId, out _);
		await ValueTask.CompletedTask.ConfigureAwait(false);
	}

	static SavedQuery ToModel(SavedQueryRecord r) => new(
		r.Id,
		r.Name,
		r.Kql,
		DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs),
		DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtMs));
}
