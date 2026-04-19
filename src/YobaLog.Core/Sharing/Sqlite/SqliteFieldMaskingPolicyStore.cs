using System.Collections.Concurrent;
using System.Collections.Immutable;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Sharing.Sqlite;

public sealed class SqliteFieldMaskingPolicyStore : IFieldMaskingPolicyStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();

	public SqliteFieldMaskingPolicyStore(IOptions<SqliteLogStoreOptions> options)
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
		foreach (var stmt in SqliteMaskingPolicySchema.AllStatements)
			await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
	}

	public async ValueTask<FieldMaskingPolicy> GetAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var builder = ImmutableDictionary.CreateBuilder<string, MaskMode>(StringComparer.Ordinal);
		await foreach (var row in db.GetTable<MaskingPolicyRecord>().AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			builder[row.Path] = (MaskMode)row.Mode;
		return new FieldMaskingPolicy(builder.ToImmutable());
	}

	public async ValueTask UpsertAsync(WorkspaceId workspaceId, IReadOnlyDictionary<string, MaskMode> modes, CancellationToken ct)
	{
		if (modes.Count == 0)
			return;

		await using var db = Open(workspaceId);
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
}
