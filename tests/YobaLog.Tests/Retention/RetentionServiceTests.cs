using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.Retention;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Retention;

public sealed class RetentionServiceTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;
	readonly ConfigApiKeyStore _apiKeys;
	static readonly WorkspaceId UserWs = WorkspaceId.Parse("retention-test");

	public RetentionServiceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-ret-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteLogStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
		_apiKeys = new ConfigApiKeyStore(Options.Create(new ApiKeyOptions
		{
			Keys = [new ApiKeyConfig { Token = "k", Workspace = UserWs.Value }],
		}));
	}

	public async Task InitializeAsync()
	{
		await _store.CreateWorkspaceAsync(UserWs, new WorkspaceSchema(), CancellationToken.None);
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static LogEventCandidate Candidate(DateTimeOffset ts, string msg = "m") => new(
		ts, LogLevel.Information, msg, msg, null, null, null, null,
		ImmutableDictionary<string, JsonElement>.Empty);

	RetentionService CreateService(int userDays = 7, int systemDays = 30) =>
		new(
			_store,
			_apiKeys,
			Options.Create(new RetentionOptions
			{
				RetentionDays = userDays,
				SystemRetentionDays = systemDays,
				RunInterval = TimeSpan.FromSeconds(1),
			}),
			NullLogger<RetentionService>.Instance);

	[Fact]
	public async Task RunPass_Deletes_EventsOlderThanRetentionDays()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[
				Candidate(now.AddDays(-10), "old"),
				Candidate(now.AddDays(-5), "mid"),
				Candidate(now.AddMinutes(-1), "fresh"),
			],
			CancellationToken.None);

		var svc = CreateService(userDays: 7);
		await svc.RunPassAsync(now, CancellationToken.None);

		var remaining = await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None);
		remaining.Should().Be(2);
	}

	[Fact]
	public async Task RunPass_SystemUsesSeparateRetention()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(WorkspaceId.System,
			[
				Candidate(now.AddDays(-20), "sys-old"),
				Candidate(now.AddDays(-5), "sys-fresh"),
			],
			CancellationToken.None);

		var svc = CreateService(userDays: 7, systemDays: 30);
		await svc.RunPassAsync(now, CancellationToken.None);

		var remaining = await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
		remaining.Should().Be(2);
	}

	[Fact]
	public async Task RunPass_OldEnoughSystemEvents_Deleted()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(WorkspaceId.System,
			[
				Candidate(now.AddDays(-40), "way-old"),
				Candidate(now.AddDays(-5), "fresh"),
			],
			CancellationToken.None);

		var svc = CreateService(userDays: 7, systemDays: 30);
		await svc.RunPassAsync(now, CancellationToken.None);

		var remaining = await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
		remaining.Should().Be(1);
	}

	[Fact]
	public async Task RunPass_Idempotent()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs,
			[Candidate(now.AddDays(-30), "old")],
			CancellationToken.None);

		var svc = CreateService(userDays: 7);
		await svc.RunPassAsync(now, CancellationToken.None);
		await svc.RunPassAsync(now, CancellationToken.None);

		var remaining = await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None);
		remaining.Should().Be(0);
	}

	[Fact]
	public async Task RunPass_IteratesAllConfiguredWorkspaces()
	{
		var otherWs = WorkspaceId.Parse("other-retention");
		await _store.CreateWorkspaceAsync(otherWs, new WorkspaceSchema(), CancellationToken.None);

		var multiApiKeys = new ConfigApiKeyStore(Options.Create(new ApiKeyOptions
		{
			Keys =
			[
				new ApiKeyConfig { Token = "k1", Workspace = UserWs.Value },
				new ApiKeyConfig { Token = "k2", Workspace = otherWs.Value },
			],
		}));

		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs, [Candidate(now.AddDays(-30), "a")], CancellationToken.None);
		await _store.AppendBatchAsync(otherWs, [Candidate(now.AddDays(-30), "b")], CancellationToken.None);

		var svc = new RetentionService(
			_store,
			multiApiKeys,
			Options.Create(new RetentionOptions { RetentionDays = 7, SystemRetentionDays = 30 }),
			NullLogger<RetentionService>.Instance);

		await svc.RunPassAsync(now, CancellationToken.None);

		(await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
		(await _store.CountAsync(otherWs, new LogQuery(PageSize: 1), CancellationToken.None)).Should().Be(0);
	}

	[Fact]
	public async Task RunPass_ConcurrentAppends_NotBlocked()
	{
		var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(UserWs, [Candidate(now.AddDays(-30), "old")], CancellationToken.None);

		var svc = CreateService();
		var retentionTask = svc.RunPassAsync(now, CancellationToken.None);
		var appendTask = _store.AppendBatchAsync(UserWs, [Candidate(now, "new")], CancellationToken.None).AsTask();

		await Task.WhenAll(retentionTask, appendTask);

		// old removed, new present
		var count = await _store.CountAsync(UserWs, new LogQuery(PageSize: 1), CancellationToken.None);
		count.Should().Be(1);
	}
}
