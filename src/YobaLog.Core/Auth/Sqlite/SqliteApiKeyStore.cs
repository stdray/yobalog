using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Admin;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Auth.Sqlite;

// DB-backed api-keys catalog. Each workspace owns its table in `<ws>.meta.db`, symmetric to
// saved queries / masking policies / share links. The hot path (ValidateAsync) hits an
// aggregated in-memory snapshot rebuilt on Initialize/Create/Delete/Drop — reads are lock-free.
//
// Plaintext tokens are never stored — only sha256(token) as hex + a 6-char prefix for UI
// identification. Plaintext is returned exactly once from CreateAsync.
public sealed class SqliteApiKeyStore : IApiKeyStore, IApiKeyAdmin
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();
	readonly Lock _sync = new();

	ImmutableDictionary<string, WorkspaceId> _tokensByHash = ImmutableDictionary<string, WorkspaceId>.Empty;
	ImmutableHashSet<WorkspaceId> _workspaces = ImmutableHashSet<WorkspaceId>.Empty;

	public SqliteApiKeyStore(IOptions<SqliteLogStoreOptions> options)
	{
		_options = options.Value;
	}

	string PathFor(WorkspaceId ws) =>
		_pathCache.GetOrAdd(ws, w => Path.Combine(_options.DataDirectory, $"{w.Value}.meta.db"));

	DataConnection Open(WorkspaceId ws) =>
		SQLiteTools.CreateDataConnection($"Data Source={PathFor(ws)};Cache=Shared");

	public IReadOnlyCollection<WorkspaceId> ConfiguredWorkspaces => _workspaces;

	public ValueTask<ApiKeyValidation> ValidateAsync(string? token, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(token))
			return ValueTask.FromResult(ApiKeyValidation.Invalid("missing api key"));

		var hash = HashToken(token);
		return _tokensByHash.TryGetValue(hash, out var ws)
			? ValueTask.FromResult(ApiKeyValidation.Valid(ws))
			: ValueTask.FromResult(ApiKeyValidation.Invalid("unknown api key"));
	}

	public async ValueTask InitializeWorkspaceAsync(WorkspaceId workspace, CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using (var db = Open(workspace))
		{
			await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
			await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
			foreach (var stmt in SqliteApiKeySchema.AllStatements)
				await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
		}

		await MergeWorkspaceIntoCacheAsync(workspace, ct).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<ApiKeyInfo>> ListAsync(WorkspaceId workspace, CancellationToken ct)
	{
		await using var db = Open(workspace);
		var rows = new List<ApiKeyInfo>();
		await foreach (var r in db.GetTable<ApiKeyRecord>().OrderBy(r => r.CreatedAtMs).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			rows.Add(ToModel(workspace, r));
		return rows;
	}

	public async ValueTask<ApiKeyCreated> CreateAsync(WorkspaceId workspace, string? title, CancellationToken ct)
	{
		var plaintext = ShortGuid.NewShortGuid().ToString();
		var hash = HashToken(plaintext);
		var prefix = plaintext[..6];
		var id = ShortGuid.NewShortGuid().ToString();
		var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
		var now = DateTimeOffset.UtcNow;

		await using (var db = Open(workspace))
		{
			await db.InsertAsync(new ApiKeyRecord
			{
				Id = id,
				TokenHash = hash,
				Prefix = prefix,
				Title = normalizedTitle,
				CreatedAtMs = now.ToUnixTimeMilliseconds(),
			}, token: ct).ConfigureAwait(false);
		}

		lock (_sync)
		{
			_tokensByHash = _tokensByHash.SetItem(hash, workspace);
			_workspaces = _workspaces.Add(workspace);
		}

		return new ApiKeyCreated(
			new ApiKeyInfo(id, prefix, workspace, normalizedTitle, now),
			plaintext);
	}

	public async ValueTask<bool> DeleteAsync(WorkspaceId workspace, string id, CancellationToken ct)
	{
		string? hashToEvict = null;
		bool noKeysLeft;

		await using (var db = Open(workspace))
		{
			var row = await db.GetTable<ApiKeyRecord>()
				.FirstOrDefaultAsync(r => r.Id == id, ct)
				.ConfigureAwait(false);
			if (row is null)
				return false;

			hashToEvict = row.TokenHash;
			await db.GetTable<ApiKeyRecord>()
				.Where(r => r.Id == id)
				.DeleteAsync(ct)
				.ConfigureAwait(false);

			noKeysLeft = await db.GetTable<ApiKeyRecord>().CountAsync(ct).ConfigureAwait(false) == 0;
		}

		lock (_sync)
		{
			if (hashToEvict is not null)
				_tokensByHash = _tokensByHash.Remove(hashToEvict);
			if (noKeysLeft)
				_workspaces = _workspaces.Remove(workspace);
		}

		return true;
	}

	public async ValueTask DropWorkspaceAsync(WorkspaceId workspace, CancellationToken ct)
	{
		SqliteConnection.ClearAllPools();
		GC.Collect();
		GC.WaitForPendingFinalizers();

		var path = PathFor(workspace);
		if (File.Exists(path))
			File.Delete(path);
		foreach (var suffix in (ReadOnlySpan<string>)["-wal", "-shm", "-journal"])
		{
			var extra = path + suffix;
			if (File.Exists(extra))
				File.Delete(extra);
		}
		_pathCache.TryRemove(workspace, out _);

		lock (_sync)
		{
			var tokens = _tokensByHash.ToBuilder();
			foreach (var (hash, ws) in _tokensByHash)
				if (ws == workspace)
					tokens.Remove(hash);
			_tokensByHash = tokens.ToImmutable();
			_workspaces = _workspaces.Remove(workspace);
		}

		await ValueTask.CompletedTask.ConfigureAwait(false);
	}

	async Task MergeWorkspaceIntoCacheAsync(WorkspaceId workspace, CancellationToken ct)
	{
		await using var db = Open(workspace);
		var rows = await db.GetTable<ApiKeyRecord>().ToListAsync(ct).ConfigureAwait(false);

		lock (_sync)
		{
			var tokens = _tokensByHash.ToBuilder();
			foreach (var (hash, ws) in _tokensByHash)
				if (ws == workspace)
					tokens.Remove(hash);

			foreach (var r in rows)
				tokens[r.TokenHash] = workspace;

			_tokensByHash = tokens.ToImmutable();
			_workspaces = rows.Count > 0 ? _workspaces.Add(workspace) : _workspaces.Remove(workspace);
		}
	}

	static ApiKeyInfo ToModel(WorkspaceId workspace, ApiKeyRecord r) =>
		new(r.Id, r.Prefix, workspace, r.Title, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs));

	static string HashToken(string token)
	{
		Span<byte> hash = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
		return Convert.ToHexStringLower(hash);
	}
}
