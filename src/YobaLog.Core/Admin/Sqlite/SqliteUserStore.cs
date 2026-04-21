using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Admin.Sqlite;

public sealed class SqliteUserStore : IUserStore
{
	readonly SqliteLogStoreOptions _options;

	public SqliteUserStore(IOptions<SqliteLogStoreOptions> options)
	{
		_options = options.Value;
	}

	string AdminDbPath => Path.Combine(_options.DataDirectory, $"{WorkspaceId.System.Value}.meta.db");

	DataConnection Open() =>
		SQLiteTools.CreateDataConnection($"Data Source={AdminDbPath};Cache=Shared");

	public async ValueTask InitializeAsync(CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using var db = Open();
		await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
		await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
		// Users live in $system.meta.db alongside Workspaces — SqliteWorkspaceStore.InitializeAsync
		// runs AllStatements (including CreateUsers) on the same DB, so this call is redundant in
		// practice. Kept idempotent so the store can be used standalone (tests, tooling).
		await db.ExecuteAsync(SqliteAdminSchema.CreateUsers, ct).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<UserInfo>> ListAsync(CancellationToken ct)
	{
		await using var db = Open();
		var rows = new List<UserInfo>();
		await foreach (var row in db.GetTable<UserRecord>().OrderBy(r => r.Username).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			rows.Add(ToModel(row));
		return rows;
	}

	public async ValueTask<bool> VerifyAsync(string username, string password, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrEmpty(username);
		ArgumentNullException.ThrowIfNull(password);

		await using var db = Open();
		var row = await db.GetTable<UserRecord>().FirstOrDefaultAsync(r => r.Username == username, ct).ConfigureAwait(false);
		// AdminPasswordHasher.Verify is constant-time on matching-length hashes; we still want a
		// dummy verify on the miss path so timing doesn't leak whether the username exists.
		return row is null
			? AdminPasswordHasher.Verify(password, DummyHash)
			: AdminPasswordHasher.Verify(password, row.PasswordHash);
	}

	public async ValueTask<UserInfo> CreateAsync(string username, string password, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrEmpty(username);
		ArgumentException.ThrowIfNullOrEmpty(password);

		await using var db = Open();
		var existing = await db.GetTable<UserRecord>().FirstOrDefaultAsync(r => r.Username == username, ct).ConfigureAwait(false);
		if (existing is not null)
			throw new InvalidOperationException($"user '{username}' already exists");

		var now = DateTimeOffset.UtcNow;
		await db.InsertAsync(new UserRecord
		{
			Username = username,
			PasswordHash = AdminPasswordHasher.Hash(password),
			CreatedAtMs = now.ToUnixTimeMilliseconds(),
		}, token: ct).ConfigureAwait(false);
		return new UserInfo(username, now);
	}

	public async ValueTask UpdatePasswordAsync(string username, string newPassword, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrEmpty(username);
		ArgumentException.ThrowIfNullOrEmpty(newPassword);

		await using var db = Open();
		var hash = AdminPasswordHasher.Hash(newPassword);
		var updated = await db.GetTable<UserRecord>()
			.Where(r => r.Username == username)
			.Set(r => r.PasswordHash, hash)
			.UpdateAsync(ct)
			.ConfigureAwait(false);
		if (updated == 0)
			throw new InvalidOperationException($"user '{username}' not found");
	}

	public async ValueTask<bool> DeleteAsync(string username, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrEmpty(username);

		await using var db = Open();
		var deleted = await db.GetTable<UserRecord>()
			.Where(r => r.Username == username)
			.DeleteAsync(ct)
			.ConfigureAwait(false);
		return deleted > 0;
	}

	static UserInfo ToModel(UserRecord r) =>
		new(r.Username, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs));

	// Precomputed hash of a throwaway password — used on the VerifyAsync miss path to keep the
	// hashing cost equal regardless of whether the username exists (prevents user-enumeration
	// via timing). Generated once at type init.
	static readonly string DummyHash = AdminPasswordHasher.Hash("dummy-timing-baseline-value");
}
