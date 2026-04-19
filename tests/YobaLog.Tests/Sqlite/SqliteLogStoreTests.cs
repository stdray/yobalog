using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Sqlite;

public sealed class SqliteLogStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("test-ws");

	public SqliteLogStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-tests-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteLogStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync()
	{
		await _store.CreateWorkspaceAsync(Ws, new WorkspaceSchema(), CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static LogEventCandidate Candidate(
		DateTimeOffset? ts = null,
		LogLevel level = LogLevel.Information,
		string msg = "m",
		string? traceId = null,
		ImmutableDictionary<string, JsonElement>? props = null) => new(
			ts ?? DateTimeOffset.UtcNow,
			level,
			msg,
			msg,
			null,
			traceId,
			null,
			null,
			props ?? ImmutableDictionary<string, JsonElement>.Empty);

	[Fact]
	public async Task Append_And_Query_RoundTrips()
	{
		await _store.AppendBatchAsync(Ws, [Candidate(msg: "hello")], CancellationToken.None);

		var q = new LogQuery(PageSize: 10);
		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, q, CancellationToken.None))
			results.Add(e);

		results.Should().HaveCount(1);
		results[0].Message.Should().Be("hello");
		results[0].Id.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task Query_OrderedByTimestampDescending()
	{
		var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var batch = Enumerable.Range(0, 5)
			.Select(i => Candidate(t0.AddMinutes(i), msg: $"m{i}"))
			.ToList();
		await _store.AppendBatchAsync(Ws, batch, CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			results.Add(e);

		results.Select(e => e.Message).Should().ContainInOrder("m4", "m3", "m2", "m1", "m0");
	}

	[Fact]
	public async Task Query_PageSize_LimitsResults()
	{
		var batch = Enumerable.Range(0, 20).Select(i => Candidate(msg: $"m{i}")).ToList();
		await _store.AppendBatchAsync(Ws, batch, CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 5), CancellationToken.None))
			results.Add(e);

		results.Should().HaveCount(5);
	}

	[Fact]
	public async Task Query_TimeRange_Filters()
	{
		var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(t0.AddMinutes(-5), msg: "before"),
				Candidate(t0, msg: "at"),
				Candidate(t0.AddMinutes(5), msg: "after"),
			],
			CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(
			PageSize: 10,
			From: t0,
			To: t0.AddMinutes(5)), CancellationToken.None))
			results.Add(e);

		results.Select(e => e.Message).Should().BeEquivalentTo(["at"]);
	}

	[Fact]
	public async Task Query_LevelFilter_GreaterThanOrEqual()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(level: LogLevel.Debug, msg: "d"),
				Candidate(level: LogLevel.Warning, msg: "w"),
				Candidate(level: LogLevel.Error, msg: "e"),
			],
			CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 10, MinLevel: LogLevel.Warning), CancellationToken.None))
			results.Add(e);

		results.Select(e => e.Message).Should().BeEquivalentTo(["w", "e"]);
	}

	[Fact]
	public async Task Query_TraceIdFilter_ExactMatch()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(traceId: "abc", msg: "a"),
				Candidate(traceId: "xyz", msg: "x"),
				Candidate(traceId: null, msg: "n"),
			],
			CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 10, TraceId: "abc"), CancellationToken.None))
			results.Add(e);

		results.Select(e => e.Message).Should().BeEquivalentTo(["a"]);
	}

	[Fact]
	public async Task Query_MessageSubstring_Filters()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(msg: "user alice logged in"),
				Candidate(msg: "user bob logged out"),
				Candidate(msg: "nothing to do"),
			],
			CancellationToken.None);

		var results = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 10, MessageSubstring: "logged"), CancellationToken.None))
			results.Add(e);

		results.Select(e => e.Message).Should().HaveCount(2);
	}

	[Fact]
	public async Task Query_Cursor_PaginatesCorrectly()
	{
		var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var batch = Enumerable.Range(0, 10)
			.Select(i => Candidate(t0.AddMinutes(i), msg: $"m{i}"))
			.ToList();
		await _store.AppendBatchAsync(Ws, batch, CancellationToken.None);

		var first = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 3), CancellationToken.None))
			first.Add(e);

		first.Should().HaveCount(3);
		var last = first[^1];
		var cursor = CursorCodec.Encode(last.Timestamp.ToUnixTimeMilliseconds(), last.Id);

		var second = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 3, Cursor: cursor), CancellationToken.None))
			second.Add(e);

		second.Should().HaveCount(3);
		first.Concat(second).Select(e => e.Message).Should().ContainInOrder("m9", "m8", "m7", "m6", "m5", "m4");
	}

	[Fact]
	public async Task Count_MatchesQuery()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(level: LogLevel.Information, msg: "i"),
				Candidate(level: LogLevel.Warning, msg: "w"),
				Candidate(level: LogLevel.Error, msg: "e"),
			],
			CancellationToken.None);

		var count = await _store.CountAsync(Ws, new LogQuery(PageSize: 1, MinLevel: LogLevel.Warning), CancellationToken.None);
		count.Should().Be(2);
	}

	[Fact]
	public async Task DeleteOlderThan_RemovesOldEvents()
	{
		var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(t0.AddDays(-30), msg: "old"),
				Candidate(t0.AddDays(-15), msg: "mid"),
				Candidate(t0, msg: "new"),
			],
			CancellationToken.None);

		var deleted = await _store.DeleteOlderThanAsync(Ws, t0.AddDays(-20), CancellationToken.None);
		deleted.Should().Be(1);

		var remaining = await _store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		remaining.Should().Be(2);
	}

	[Fact]
	public async Task GetStats_ReturnsCountAndSize()
	{
		var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(Ws,
			[
				Candidate(t0.AddDays(-5), msg: "a"),
				Candidate(t0, msg: "b"),
			],
			CancellationToken.None);

		var stats = await _store.GetStatsAsync(Ws, CancellationToken.None);
		stats.EventCount.Should().Be(2);
		stats.SizeBytes.Should().BeGreaterThan(0);
		stats.OldestEvent.Should().Be(t0.AddDays(-5));
	}

	[Fact]
	public async Task Properties_RoundtripJson()
	{
		var props = ImmutableDictionary<string, JsonElement>.Empty
			.Add("User", JsonDocument.Parse(@"""alice""").RootElement.Clone())
			.Add("Count", JsonDocument.Parse("42").RootElement.Clone());

		await _store.AppendBatchAsync(Ws, [Candidate(props: props)], CancellationToken.None);

		LogEvent? stored = null;
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None))
			stored = e;

		stored.Should().NotBeNull();
		stored!.Properties.Should().ContainKey("User");
		stored.Properties["User"].GetString().Should().Be("alice");
		stored.Properties["Count"].GetInt32().Should().Be(42);
	}

	[Fact]
	public async Task WorkspaceIsolation_DataDoesNotLeak()
	{
		var otherWs = WorkspaceId.Parse("other-ws");
		await _store.CreateWorkspaceAsync(otherWs, new WorkspaceSchema(), CancellationToken.None);

		await _store.AppendBatchAsync(Ws, [Candidate(msg: "in-main")], CancellationToken.None);
		await _store.AppendBatchAsync(otherWs, [Candidate(msg: "in-other")], CancellationToken.None);

		var mainCount = await _store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		var otherCount = await _store.CountAsync(otherWs, new LogQuery(PageSize: 1), CancellationToken.None);
		mainCount.Should().Be(1);
		otherCount.Should().Be(1);

		var mainResults = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			mainResults.Add(e);
		mainResults.Single().Message.Should().Be("in-main");
	}

	[Fact]
	public async Task DropWorkspace_RemovesFile()
	{
		var tempWs = WorkspaceId.Parse("temp-drop");
		await _store.CreateWorkspaceAsync(tempWs, new WorkspaceSchema(), CancellationToken.None);
		await _store.AppendBatchAsync(tempWs, [Candidate(msg: "x")], CancellationToken.None);

		await _store.DropWorkspaceAsync(tempWs, CancellationToken.None);

		var path = Path.Combine(_tempDir, "temp-drop.logs.db");
		File.Exists(path).Should().BeFalse();
	}
}
