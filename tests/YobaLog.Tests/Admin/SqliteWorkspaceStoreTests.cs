using Microsoft.Extensions.Options;
using YobaLog.Core.Admin;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Auth.Sqlite;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Sharing;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing.Sqlite;

namespace YobaLog.Tests.Admin;

public sealed class SqliteWorkspaceStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStoreOptions _options;
	readonly SqliteLogStore _logStore;
	readonly SqliteSpanStore _spans;
	readonly SqliteApiKeyStore _apiKeys;
	readonly SqliteSavedQueryStore _savedQueries;
	readonly SqliteFieldMaskingPolicyStore _masking;
	readonly SqliteShareLinkStore _shareLinks;
	readonly SqliteWorkspaceStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("admin-create");

	public SqliteWorkspaceStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-ws-admin-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_options = new SqliteLogStoreOptions { DataDirectory = _tempDir };
		var opts = Options.Create(_options);
		_logStore = new SqliteLogStore(opts);
		_spans = new SqliteSpanStore(opts);
		_apiKeys = new SqliteApiKeyStore(opts);
		_savedQueries = new SqliteSavedQueryStore(opts);
		_masking = new SqliteFieldMaskingPolicyStore(opts);
		_shareLinks = new SqliteShareLinkStore(opts);
		_store = new SqliteWorkspaceStore(opts, _logStore, _spans, _apiKeys, _savedQueries, _masking, _shareLinks);
	}

	public async Task InitializeAsync() => await _store.InitializeAsync(CancellationToken.None);

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task CreateAsync_Initializes_AllMetaTables()
	{
		await _store.CreateAsync(Ws, CancellationToken.None);

		// Each meta store's list/get must not throw "no such table" — this is the bug that bit
		// admin-created workspaces in prod (GET /ws/{newly-created} → SqliteException on
		// SavedQueries because only the api-keys meta was initialized).
		var savedQueries = await _savedQueries.ListAsync(Ws, CancellationToken.None);
		savedQueries.Should().BeEmpty();

		var policy = await _masking.GetAsync(Ws, CancellationToken.None);
		policy.Modes.Should().BeEmpty();

		var shareLink = await _shareLinks.GetAsync(Ws, "nonexistent", CancellationToken.None);
		shareLink.Should().BeNull();

		var apiKeys = await _apiKeys.ListAsync(Ws, CancellationToken.None);
		apiKeys.Should().BeEmpty();
	}

	[Fact]
	public async Task DeleteAsync_Drops_AllMetaFiles()
	{
		await _store.CreateAsync(Ws, CancellationToken.None);

		var deleted = await _store.DeleteAsync(Ws, CancellationToken.None);
		deleted.Should().BeTrue();

		// Both logs.db and meta.db should be gone (api-keys + saved queries + masking + share
		// links all live in meta.db → deleting the file removes all four).
		File.Exists(Path.Combine(_tempDir, $"{Ws.Value}.logs.db")).Should().BeFalse();
		File.Exists(Path.Combine(_tempDir, $"{Ws.Value}.meta.db")).Should().BeFalse();
	}
}
