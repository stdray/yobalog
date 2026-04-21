using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YobaLog.Core.Admin;
using YobaLog.Core.Retention;
using YobaLog.Core.Retention.Sqlite;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing.Sqlite;

namespace YobaLog.Tests.Retention;

public sealed class RetentionServiceTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;
	readonly SqliteSpanStore _spans;
	readonly SqliteSavedQueryStore _savedQueries;
	readonly SqliteShareLinkStore _shareLinks;
	readonly SqliteRetentionPolicyStore _policyStore;
	readonly InMemoryWorkspaceStore _workspaces;
	static readonly WorkspaceId UserWs = WorkspaceId.Parse("retention-test");

	public RetentionServiceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-ret-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		var storeOpts = Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir });
		_store = new SqliteLogStore(storeOpts);
		_spans = new SqliteSpanStore(storeOpts);
		_savedQueries = new SqliteSavedQueryStore(storeOpts);
		_shareLinks = new SqliteShareLinkStore(storeOpts);
		_policyStore = new SqliteRetentionPolicyStore(storeOpts);
		_workspaces = new InMemoryWorkspaceStore(UserWs);
	}

	public async Task InitializeAsync()
	{
		await _store.CreateWorkspaceAsync(UserWs, new WorkspaceSchema(), CancellationToken.None);
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), CancellationToken.None);
		await _spans.CreateWorkspaceAsync(UserWs, CancellationToken.None);
		await _spans.CreateWorkspaceAsync(WorkspaceId.System, CancellationToken.None);
		await _savedQueries.InitializeWorkspaceAsync(UserWs, CancellationToken.None);
		await _policyStore.InitializeAsync(CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static LogEventCandidate Candidate(DateTimeOffset ts, LogLevel level = LogLevel.Information, string msg = "m") => new(
		ts, level, msg, msg, null, null, null, null,
		ImmutableDictionary<string, JsonElement>.Empty);

	RetentionService CreateService(
		int defaultDays = 7,
		int systemDays = 30,
		IReadOnlyList<RetentionPolicy>? policies = null,
		IWorkspaceStore? workspaces = null) =>
		new(
			_store,
			_spans,
			_savedQueries,
			_shareLinks,
			workspaces ?? _workspaces,
			_policyStore,
			Options.Create(new RetentionOptions
			{
				DefaultRetainDays = defaultDays,
				SystemRetainDays = systemDays,
				RunInterval = TimeSpan.FromSeconds(1),
				Policies = policies ?? [],
			}),
			NullLogger<RetentionService>.Instance);

	[Fact]
	public async Task NoPolicies_UsesDefault()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-10), msg: "old"),
				Candidate(now.AddDays(-5), msg: "mid"),
				Candidate(now.AddMinutes(-1), msg: "fresh"),
			],
			CancellationToken.None);

		await CreateService(defaultDays: 7).RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(2);
	}

	[Fact]
	public async Task SystemUsesSeparateRetention()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(WorkspaceId.System,
			[
				Candidate(now.AddDays(-40), msg: "way-old"),
				Candidate(now.AddDays(-5), msg: "fresh"),
			],
			CancellationToken.None);

		await CreateService(defaultDays: 7, systemDays: 30).RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(1);
	}

	[Fact]
	public async Task Policy_KeepsErrorsLonger()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-45), LogLevel.Error, "old-error"),
				Candidate(now.AddDays(-45), LogLevel.Information, "old-info"),
				Candidate(now.AddDays(-10), LogLevel.Error, "recent-error"),
				Candidate(now.AddDays(-3), LogLevel.Information, "fresh-info"),
			],
			CancellationToken.None);

		await _savedQueries.UpsertAsync(UserWs, "errors", "events | where Level >= 4", CancellationToken.None);

		var svc = CreateService(policies:
		[
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "errors", RetainDays = 90 },
		]);
		await svc.RunPassAsync(now, CancellationToken.None);

		// Info @ -45d is NOT matched by any policy and policies-defined → no default sweep for this workspace.
		// Error @ -45d is matched, but still younger than 90d — kept.
		// Error @ -10d kept; Info @ -3d kept.
		var count = await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None);
		count.Should().Be(4);
	}

	[Fact]
	public async Task Policy_SweepsExpiredWithinCategory()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-100), LogLevel.Error, "ancient-error"),
				Candidate(now.AddDays(-10), LogLevel.Error, "recent-error"),
			],
			CancellationToken.None);

		await _savedQueries.UpsertAsync(UserWs, "errors", "events | where Level >= 4", CancellationToken.None);

		var svc = CreateService(policies:
		[
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "errors", RetainDays = 90 },
		]);
		await svc.RunPassAsync(now, CancellationToken.None);

		var messages = new List<string>();
		await foreach (var e in _store.QueryAsync(UserWs, new LogQuery(PageSize: 10), CancellationToken.None))
			messages.Add(e.Message);
		messages.Should().BeEquivalentTo(["recent-error"]);
	}

	[Fact]
	public async Task MultiplePolicies_EachAppliesIndependently()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-100), LogLevel.Error, "old-error"),
				Candidate(now.AddDays(-45), LogLevel.Warning, "old-warning"),
				Candidate(now.AddDays(-10), LogLevel.Warning, "recent-warning"),
			],
			CancellationToken.None);

		await _savedQueries.UpsertAsync(UserWs, "errors", "events | where Level >= 4", CancellationToken.None);
		await _savedQueries.UpsertAsync(UserWs, "warnings", "events | where Level == 3", CancellationToken.None);

		var svc = CreateService(policies:
		[
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "errors", RetainDays = 90 },
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "warnings", RetainDays = 30 },
		]);
		await svc.RunPassAsync(now, CancellationToken.None);

		var messages = new List<string>();
		await foreach (var e in _store.QueryAsync(UserWs, new LogQuery(PageSize: 10), CancellationToken.None))
			messages.Add(e.Message);
		messages.Should().BeEquivalentTo(["recent-warning"]);
	}

	[Fact]
	public async Task DbPolicy_Overrides_ConfigPolicy_When_Both_Defined()
	{
		// When the DB store has any policy for a workspace, config Retention:Policies[] is ignored
		// for that workspace. Proves the DB = source of truth, config = bootstrap fallback.
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-20), LogLevel.Error, "err"),
			],
			CancellationToken.None);
		await _savedQueries.UpsertAsync(UserWs, "errors", "events | where Level >= 4", CancellationToken.None);

		// Config says keep errors 90 days; DB says keep 7 days. DB wins → the -20d error should be swept.
		await _policyStore.UpsertAsync(
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "errors", RetainDays = 7 },
			CancellationToken.None);

		var svc = CreateService(policies:
		[
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "errors", RetainDays = 90 },
		]);
		await svc.RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
	}

	[Fact]
	public async Task Policy_MissingSavedQuery_LogsAndContinues()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[Candidate(now.AddDays(-5), msg: "untouched")],
			CancellationToken.None);

		var svc = CreateService(policies:
		[
			new RetentionPolicy { Workspace = UserWs.Value, SavedQuery = "nonexistent", RetainDays = 7 },
		]);

		var act = () => svc.RunPassAsync(now, CancellationToken.None);
		await act.Should().NotThrowAsync();

		(await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(1);
	}

	[Fact]
	public async Task RunPass_Idempotent()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[Candidate(now.AddDays(-30), msg: "old")],
			CancellationToken.None);

		var svc = CreateService(defaultDays: 7);
		await svc.RunPassAsync(now, CancellationToken.None);
		await svc.RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
	}

	[Fact]
	public async Task AdminCreatedWorkspace_WithoutApiKeys_IsSweptByRetention()
	{
		// Regression for tech-debt #4: before the fix, RetentionService iterated
		// IApiKeyStore.ConfiguredWorkspaces, so a workspace created via /admin without
		// any API keys was invisible to the sweep and its .logs.db grew forever.
		var keylessWs = WorkspaceId.Parse("keyless-ws");
		await _store.CreateWorkspaceAsync(keylessWs, new WorkspaceSchema(), CancellationToken.None);
		await _savedQueries.InitializeWorkspaceAsync(keylessWs, CancellationToken.None);

		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(keylessWs,
			[Candidate(now.AddDays(-30), msg: "stale")],
			CancellationToken.None);

		var workspaces = new InMemoryWorkspaceStore(keylessWs);
		await CreateService(defaultDays: 7, workspaces: workspaces).RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(keylessWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
	}

	[Fact]
	public async Task SystemWorkspace_IsNotListedByStore_ButStillSwept()
	{
		// Defensive: even if a buggy IWorkspaceStore returns $system in ListAsync,
		// RunPassAsync filters it out (IsSystem) and sweeps it via SweepSystemAsync.
		// Here we validate the normal path — store lists only user ws, $system swept via its own branch.
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(WorkspaceId.System,
			[Candidate(now.AddDays(-40), msg: "way-old")],
			CancellationToken.None);

		await CreateService(defaultDays: 7, systemDays: 30).RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
	}
}

sealed class InMemoryWorkspaceStore(params WorkspaceId[] ids) : IWorkspaceStore
{
	readonly IReadOnlyList<WorkspaceInfo> _infos =
		[.. ids.Select(id => new WorkspaceInfo(id, DateTimeOffset.UtcNow))];

	public ValueTask InitializeAsync(CancellationToken ct) => ValueTask.CompletedTask;

	public ValueTask<IReadOnlyList<WorkspaceInfo>> ListAsync(CancellationToken ct) => new(_infos);

	public ValueTask<WorkspaceInfo?> GetAsync(WorkspaceId id, CancellationToken ct) =>
		new(_infos.FirstOrDefault(w => w.Id == id));

	public ValueTask<WorkspaceInfo> CreateAsync(WorkspaceId id, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask<bool> DeleteAsync(WorkspaceId id, CancellationToken ct) =>
		throw new NotSupportedException();
}
