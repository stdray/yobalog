using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Sharing.Sqlite;

public sealed class SqliteShareLinkStore : IShareLinkStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();

	public SqliteShareLinkStore(IOptions<SqliteLogStoreOptions> options)
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
		foreach (var stmt in SqliteShareLinkSchema.AllStatements)
			await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
	}

	public async ValueTask<ShareLink> CreateAsync(
		WorkspaceId workspaceId,
		string kql,
		DateTimeOffset expiresAt,
		ImmutableArray<string> columns,
		ImmutableDictionary<string, MaskMode> modes,
		CancellationToken ct)
	{
		var id = ShortGuid.NewShortGuid().ToString();
		var salt = RandomNumberGenerator.GetBytes(16);
		var now = DateTimeOffset.UtcNow;

		var columnsArr = columns.IsDefault ? [] : columns.ToArray();
		var modesDict = modes.ToDictionary(kv => kv.Key, kv => (int)kv.Value);

		await using var db = Open(workspaceId);
		await db.InsertAsync(new ShareLinkRecord
		{
			Id = id,
			Kql = kql,
			CreatedAtMs = now.ToUnixTimeMilliseconds(),
			ExpiresAtMs = expiresAt.ToUnixTimeMilliseconds(),
			Salt = salt,
			ColumnsJson = JsonSerializer.Serialize(columnsArr),
			ModesJson = JsonSerializer.Serialize(modesDict),
		}, token: ct).ConfigureAwait(false);

		return new ShareLink(id, kql, now, expiresAt, [.. salt], [.. columnsArr], modes);
	}

	public async ValueTask<ShareLink?> GetAsync(WorkspaceId workspaceId, string id, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var row = await db.GetTable<ShareLinkRecord>()
			.FirstOrDefaultAsync(r => r.Id == id, ct)
			.ConfigureAwait(false);
		return row is null ? null : ToModel(row);
	}

	public async ValueTask<bool> DeleteAsync(WorkspaceId workspaceId, string id, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var deleted = await db.GetTable<ShareLinkRecord>()
			.Where(r => r.Id == id)
			.DeleteAsync(ct)
			.ConfigureAwait(false);
		return deleted > 0;
	}

	public async ValueTask<int> DeleteExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var cutoff = now.ToUnixTimeMilliseconds();
		return await db.GetTable<ShareLinkRecord>()
			.Where(r => r.ExpiresAtMs < cutoff)
			.DeleteAsync(ct)
			.ConfigureAwait(false);
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

	static ShareLink ToModel(ShareLinkRecord r)
	{
		var columns = JsonSerializer.Deserialize<string[]>(r.ColumnsJson) ?? [];
		var modes = JsonSerializer.Deserialize<Dictionary<string, int>>(r.ModesJson) ?? [];
		var modesBuilder = ImmutableDictionary.CreateBuilder<string, MaskMode>(StringComparer.Ordinal);
		foreach (var (k, v) in modes)
			modesBuilder[k] = (MaskMode)v;

		return new ShareLink(
			r.Id,
			r.Kql,
			DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs),
			DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresAtMs),
			[.. r.Salt],
			[.. columns],
			modesBuilder.ToImmutable());
	}
}
