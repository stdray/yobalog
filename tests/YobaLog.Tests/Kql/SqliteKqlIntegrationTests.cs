using System.Collections.Immutable;
using System.Text.Json;
using Kusto.Language;
using MsOptions = Microsoft.Extensions.Options.Options;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

public sealed class SqliteKqlIntegrationTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("kql-integration");

	public SqliteKqlIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-kql-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteLogStore(MsOptions.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync()
	{
		await _store.CreateWorkspaceAsync(Ws, new WorkspaceSchema(), CancellationToken.None);
		await _store.AppendBatchAsync(Ws, Seed, CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static readonly IReadOnlyList<LogEventCandidate> Seed =
	[
		Mk(1, LogLevel.Information, "hello world", trace: "t1"),
		Mk(2, LogLevel.Error, "boom", trace: "t2"),
		Mk(3, LogLevel.Warning, "meh", trace: "t1"),
		Mk(4, LogLevel.Error, "crash on Earth"),
		Mk(5, LogLevel.Debug, "starting", trace: "t3"),
	];

	static LogEventCandidate Mk(int secs, LogLevel level, string msg, string? trace = null,
		ImmutableDictionary<string, JsonElement>? props = null) => new(
			new DateTimeOffset(2026, 4, 19, 10, secs, 0, TimeSpan.Zero),
			level,
			msg,
			msg,
			null,
			trace,
			null,
			null,
			props ?? ImmutableDictionary<string, JsonElement>.Empty);

	static ImmutableDictionary<string, JsonElement> Props(string json) =>
		JsonDocument.Parse(json).RootElement.EnumerateObject()
			.Aggregate(
				ImmutableDictionary<string, JsonElement>.Empty,
				(acc, p) => acc.Add(p.Name, p.Value.Clone()));

	async Task<IReadOnlyList<string>> RunAsync(string kql)
	{
		var code = KustoCode.Parse(kql);
		var result = new List<string>();
		await foreach (var e in _store.QueryKqlAsync(Ws, code, CancellationToken.None))
			result.Add(e.Message);
		return result;
	}

	[Fact]
	public async Task WhereLevel_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level == 4");
		messages.Should().BeEquivalentTo(["boom", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereLevel_OrderingTranslatesToSql()
	{
		var messages = await RunAsync("events | where Level >= 3");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereTraceId_Equality()
	{
		var messages = await RunAsync("events | where TraceId == 't1'");
		messages.Should().BeEquivalentTo(["hello world", "meh"]);
	}

	[Fact]
	public async Task Take_LimitsRowsAgainstSqlite()
	{
		var messages = await RunAsync("events | take 2");
		messages.Should().HaveCount(2);
	}

	[Fact]
	public async Task OrderBy_TranslatesToSql()
	{
		var messages = await RunAsync("events | order by Id asc");
		messages.Should().ContainInOrder("hello world", "boom", "meh", "crash on Earth", "starting");
	}

	[Fact]
	public async Task AndCombinator_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level == 4 and TraceId == 't2'");
		messages.Should().BeEquivalentTo(["boom"]);
	}

	[Fact]
	public async Task MessageContains_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Message contains 'boom'");
		messages.Should().Contain("boom");
	}

	[Fact]
	public async Task TimestampGte_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Timestamp >= datetime(2026-04-19T10:03:00Z)");
		messages.Should().BeEquivalentTo(["meh", "crash on Earth", "starting"]);
	}

	[Fact]
	public async Task TimestampLt_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Timestamp < datetime(2026-04-19T10:03:00Z)");
		messages.Should().BeEquivalentTo(["hello world", "boom"]);
	}

	[Fact]
	public async Task MessageHas_UsesFts5()
	{
		var messages = await RunAsync("events | where Message has 'boom'");
		messages.Should().BeEquivalentTo(["boom"]);
	}

	[Fact]
	public async Task MessageHas_WordBoundary_IgnoresPartial()
	{
		// "boom" should match "boom" but NOT a substring of "booming" (word-boundary).
		await _store.AppendBatchAsync(Ws,
			[Mk(20, LogLevel.Information, "booming business")],
			CancellationToken.None);

		var messages = await RunAsync("events | where Message has 'boom'");
		messages.Should().NotContain("booming business");
		messages.Should().Contain("boom");
	}

	[Fact]
	public async Task MessageHas_CaseInsensitive()
	{
		await _store.AppendBatchAsync(Ws,
			[Mk(25, LogLevel.Information, "THUNDER strike")],
			CancellationToken.None);

		var messages = await RunAsync("events | where Message has 'thunder'");
		messages.Should().Contain("THUNDER strike");
	}

	[Fact]
	public async Task TimestampRange_TranslatesToSql()
	{
		var messages = await RunAsync(
			"events | where Timestamp >= datetime(2026-04-19T10:02:00Z) and Timestamp < datetime(2026-04-19T10:05:00Z)");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task PropertiesEq_TranslatesToJsonExtract()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Mk(30, LogLevel.Information, "login", props: Props("""{"user":"alice","region":"eu"}""")),
				Mk(31, LogLevel.Information, "login", props: Props("""{"user":"bob","region":"us"}""")),
			],
			CancellationToken.None);

		var messages = await RunAsync("events | where Properties.user == 'alice'");
		messages.Should().BeEquivalentTo(["login"]);
	}

	[Fact]
	public async Task PropertiesNe_TranslatesToJsonExtract()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Mk(40, LogLevel.Information, "x", props: Props("""{"user":"alice"}""")),
				Mk(41, LogLevel.Information, "y", props: Props("""{"user":"bob"}""")),
			],
			CancellationToken.None);

		var messages = await RunAsync("events | where Properties.user != 'alice'");
		messages.Should().Contain("y").And.NotContain("x");
	}

	[Fact]
	public async Task PropertiesContains_TranslatesToJsonExtract()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Mk(50, LogLevel.Information, "e1", props: Props("""{"email":"alice@example.com"}""")),
				Mk(51, LogLevel.Information, "e2", props: Props("""{"email":"bob@other.org"}""")),
			],
			CancellationToken.None);

		var messages = await RunAsync("events | where Properties.email contains 'example'");
		messages.Should().BeEquivalentTo(["e1"]);
	}

	[Fact]
	public async Task SummarizeByProperty_GroupsViaJsonExtract()
	{
		await _store.AppendBatchAsync(Ws,
			[
				Mk(55, LogLevel.Information, "a", props: Props("""{"SourceContext":"Web"}""")),
				Mk(56, LogLevel.Information, "b", props: Props("""{"SourceContext":"Web"}""")),
				Mk(57, LogLevel.Information, "c", props: Props("""{"SourceContext":"Worker"}""")),
			],
			CancellationToken.None);

		var code = KustoCode.Parse("events | summarize count() by Properties.SourceContext");
		var result = await _store.QueryKqlResultAsync(Ws, code, CancellationToken.None);

		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Properties.SourceContext", "count_");
		var byCtx = rows
			.Where(r => r[0] is string)
			.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		byCtx["Web"].Should().Be(2);
		byCtx["Worker"].Should().Be(1);
	}
}
