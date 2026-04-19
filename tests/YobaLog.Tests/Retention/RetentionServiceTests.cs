using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Retention;

public sealed class RetentionServiceTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;
	readonly SqliteSavedQueryStore _savedQueries;
	readonly ConfigApiKeyStore _apiKeys;
	static readonly WorkspaceId UserWs = WorkspaceId.Parse("retention-test");

	public RetentionServiceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-ret-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		var storeOpts = Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir });
		_store = new SqliteLogStore(storeOpts);
		_savedQueries = new SqliteSavedQueryStore(storeOpts);
		_apiKeys = new ConfigApiKeyStore(Options.Create(new ApiKeyOptions
		{
			Keys = [new ApiKeyConfig { Token = "k", Workspace = UserWs.Value }],
		}));
	}

	public async Task InitializeAsync()
	{
		await _store.CreateWorkspaceAsync(UserWs, new WorkspaceSchema(), CancellationToken.None);
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), CancellationToken.None);
		await _savedQueries.InitializeWorkspaceAsync(UserWs, CancellationToken.None);
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
		IApiKeyStore? apiKeys = null) =>
		new(
			_store,
			_savedQueries,
			apiKeys ?? _apiKeys,
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

		await _savedQueries.UpsertAsync(UserWs, "errors", "LogEvents | where Level >= 4", CancellationToken.None);

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

		await _savedQueries.UpsertAsync(UserWs, "errors", "LogEvents | where Level >= 4", CancellationToken.None);

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

		await _savedQueries.UpsertAsync(UserWs, "errors", "LogEvents | where Level >= 4", CancellationToken.None);
		await _savedQueries.UpsertAsync(UserWs, "warnings", "LogEvents | where Level == 3", CancellationToken.None);

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
}
