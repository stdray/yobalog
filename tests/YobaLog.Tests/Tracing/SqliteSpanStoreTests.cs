using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing;
using YobaLog.Core.Tracing.Sqlite;

namespace YobaLog.Tests.Tracing;

// Integration-level tests for SqliteSpanStore against a real .traces.db file. Coverage:
// schema creation, append → get-by-trace-id round-trip, retention, count, workspace drop.
public sealed class SqliteSpanStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteSpanStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("spans-test");

	public SqliteSpanStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-spans-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteSpanStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync()
	{
		await _store.InitializeAsync(CancellationToken.None);
		await _store.CreateWorkspaceAsync(Ws, CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static Span MakeSpan(
		string spanId,
		string traceId,
		DateTimeOffset? startTime = null,
		string name = "op",
		string? parentSpanId = null,
		TimeSpan? duration = null,
		SpanStatusCode status = SpanStatusCode.Unset)
	{
		return new Span(
			SpanId: spanId,
			TraceId: traceId,
			ParentSpanId: parentSpanId,
			Name: name,
			Kind: SpanKind.Internal,
			StartTime: startTime ?? DateTimeOffset.UtcNow,
			Duration: duration ?? TimeSpan.FromMilliseconds(42),
			Status: status,
			StatusDescription: status == SpanStatusCode.Error ? "boom" : null,
			Attributes: ImmutableDictionary<string, JsonElement>.Empty,
			Events: [],
			Links: []);
	}

	[Fact]
	public async Task Append_Then_GetByTraceId_Returns_Spans_Ordered_By_StartTime()
	{
		var traceId = "0102030405060708090a0b0c0d0e0f00";
		var t0 = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("aaaaaaaaaaaaaaaa", traceId, t0.AddMilliseconds(100), name: "later"),
				MakeSpan("bbbbbbbbbbbbbbbb", traceId, t0, name: "earliest"),
				MakeSpan("cccccccccccccccc", traceId, t0.AddMilliseconds(50), name: "middle"),
			], CancellationToken.None);

		var spans = await _store.GetByTraceIdAsync(Ws, traceId, CancellationToken.None);
		spans.Select(s => s.Name).Should().ContainInOrder("earliest", "middle", "later");
	}

	[Fact]
	public async Task GetByTraceId_ReturnsEmpty_ForUnknownTrace()
	{
		var spans = await _store.GetByTraceIdAsync(Ws, "deadbeefdeadbeefdeadbeefdeadbeef", CancellationToken.None);
		spans.Should().BeEmpty();
	}

	[Fact]
	public async Task Append_Preserves_All_Fields_RoundTrip()
	{
		var traceId = "aabbccddeeff00112233445566778899";
		var start = new DateTimeOffset(2026, 4, 21, 12, 0, 0, 123, TimeSpan.Zero);
		var duration = TimeSpan.FromMilliseconds(250);

		var attrs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		using (var doc = JsonDocument.Parse("\"value-1\"")) attrs["key1"] = doc.RootElement.Clone();
		using (var doc = JsonDocument.Parse("42")) attrs["key2"] = doc.RootElement.Clone();

		var span = new Span(
			SpanId: "1111222233334444",
			TraceId: traceId,
			ParentSpanId: "5555666677778888",
			Name: "db.query",
			Kind: SpanKind.Client,
			StartTime: start,
			Duration: duration,
			Status: SpanStatusCode.Error,
			StatusDescription: "connection timed out",
			Attributes: attrs.ToImmutable(),
			Events: [],
			Links: []);

		await _store.AppendBatchAsync(Ws, [span], CancellationToken.None);

		var spans = await _store.GetByTraceIdAsync(Ws, traceId, CancellationToken.None);
		spans.Should().HaveCount(1);
		var r = spans[0];

		r.SpanId.Should().Be("1111222233334444");
		r.TraceId.Should().Be(traceId);
		r.ParentSpanId.Should().Be("5555666677778888");
		r.Name.Should().Be("db.query");
		r.Kind.Should().Be(SpanKind.Client);
		r.Status.Should().Be(SpanStatusCode.Error);
		r.StatusDescription.Should().Be("connection timed out");
		// Duration survives round-trip within nanosecond precision.
		r.Duration.Should().BeCloseTo(duration, TimeSpan.FromMilliseconds(1));
		r.Attributes["key1"].GetString().Should().Be("value-1");
		r.Attributes["key2"].GetInt32().Should().Be(42);
	}

	[Fact]
	public async Task DeleteOlderThanAsync_Prunes_By_StartTime()
	{
		var traceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
		var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("11111111aaaaaaaa", traceId, now.AddDays(-10), name: "old"),
				MakeSpan("22222222aaaaaaaa", traceId, now.AddDays(-5), name: "mid"),
				MakeSpan("33333333aaaaaaaa", traceId, now.AddMinutes(-1), name: "fresh"),
			], CancellationToken.None);

		var cutoff = now.AddDays(-7);
		var deleted = await _store.DeleteOlderThanAsync(Ws, cutoff, CancellationToken.None);
		deleted.Should().Be(1);

		var remaining = await _store.GetByTraceIdAsync(Ws, traceId, CancellationToken.None);
		remaining.Select(s => s.Name).Should().BeEquivalentTo(["mid", "fresh"]);
	}

	[Fact]
	public async Task CountAsync_Returns_Total_SpanCount()
	{
		(await _store.CountAsync(Ws, CancellationToken.None)).Should().Be(0);

		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("aaaa000000000000", "trace-a".PadRight(32, '0')),
				MakeSpan("bbbb000000000000", "trace-b".PadRight(32, '0')),
			], CancellationToken.None);

		(await _store.CountAsync(Ws, CancellationToken.None)).Should().Be(2);
	}

	[Fact]
	public async Task Workspaces_Are_Isolated_On_Disk()
	{
		var wsA = WorkspaceId.Parse("spans-iso-a");
		var wsB = WorkspaceId.Parse("spans-iso-b");
		await _store.CreateWorkspaceAsync(wsA, CancellationToken.None);
		await _store.CreateWorkspaceAsync(wsB, CancellationToken.None);

		await _store.AppendBatchAsync(wsA, [MakeSpan("aa11000000000000", "trace".PadRight(32, '0'))], CancellationToken.None);
		await _store.AppendBatchAsync(wsB, [MakeSpan("bb22000000000000", "trace".PadRight(32, '0'))], CancellationToken.None);

		var inA = await _store.GetByTraceIdAsync(wsA, "trace".PadRight(32, '0'), CancellationToken.None);
		var inB = await _store.GetByTraceIdAsync(wsB, "trace".PadRight(32, '0'), CancellationToken.None);

		inA.Should().ContainSingle().Which.SpanId.Should().Be("aa11000000000000");
		inB.Should().ContainSingle().Which.SpanId.Should().Be("bb22000000000000");

		// Physical evidence: two separate .traces.db files.
		File.Exists(Path.Combine(_tempDir, "spans-iso-a.traces.db")).Should().BeTrue();
		File.Exists(Path.Combine(_tempDir, "spans-iso-b.traces.db")).Should().BeTrue();
	}

	[Fact]
	public async Task DropWorkspaceAsync_Removes_Traces_Db_File()
	{
		var ws = WorkspaceId.Parse("spans-drop");
		await _store.CreateWorkspaceAsync(ws, CancellationToken.None);
		await _store.AppendBatchAsync(ws, [MakeSpan("dd00000000000000", "trace".PadRight(32, '0'))], CancellationToken.None);
		var path = Path.Combine(_tempDir, "spans-drop.traces.db");
		File.Exists(path).Should().BeTrue();

		await _store.DropWorkspaceAsync(ws, CancellationToken.None);
		File.Exists(path).Should().BeFalse();
	}

	[Fact]
	public async Task ListRecentTracesAsync_Aggregates_OneRow_Per_TraceId_NewestFirst()
	{
		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);

		// Trace A: 2 spans, root + child, total 200ms
		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("a000000000000001", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", t0, name: "GET /api/a", duration: TimeSpan.FromMilliseconds(200)),
				MakeSpan("a000000000000002", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", t0.AddMilliseconds(50), name: "db.a", parentSpanId: "a000000000000001", duration: TimeSpan.FromMilliseconds(80)),
			], CancellationToken.None);

		// Trace B: 1 span, error
		await _store.AppendBatchAsync(Ws,
			[MakeSpan("b000000000000001", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1", t0.AddMilliseconds(500), name: "POST /api/b", status: SpanStatusCode.Error)],
			CancellationToken.None);

		// Trace C: 1 span, OK, latest
		await _store.AppendBatchAsync(Ws,
			[MakeSpan("c000000000000001", "ccccccccccccccccccccccccccccccc1", t0.AddSeconds(2), name: "GET /api/c", status: SpanStatusCode.Ok)],
			CancellationToken.None);

		var traces = await _store.ListRecentTracesAsync(Ws, new TracesQuery(PageSize: 10), CancellationToken.None);
		traces.Should().HaveCount(3);

		// Newest first: C → B → A.
		traces[0].TraceId.Should().Be("ccccccccccccccccccccccccccccccc1");
		traces[0].RootName.Should().Be("GET /api/c");
		traces[0].WorstStatus.Should().Be(SpanStatusCode.Ok);

		traces[1].TraceId.Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1");
		traces[1].WorstStatus.Should().Be(SpanStatusCode.Error);

		traces[2].TraceId.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1");
		traces[2].RootName.Should().Be("GET /api/a"); // root has ParentSpanId == null
		traces[2].SpanCount.Should().Be(2);
		traces[2].Duration.TotalMilliseconds.Should().BeApproximately(200, 5); // tolerance for ns→ms rounding
	}

	[Fact]
	public async Task ListRecentTracesAsync_CursorPagination_Skips_Already_Seen()
	{
		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);
		for (var i = 0; i < 5; i++)
		{
			var traceId = $"trace{i}".PadRight(32, 'f');
			await _store.AppendBatchAsync(Ws,
				[MakeSpan($"span{i}".PadRight(16, '0'), traceId, t0.AddSeconds(i), name: $"op-{i}")],
				CancellationToken.None);
		}

		// First page: 2 newest (op-4, op-3).
		var page1 = await _store.ListRecentTracesAsync(Ws, new TracesQuery(PageSize: 2), CancellationToken.None);
		page1.Select(t => t.RootName).Should().ContainInOrder("op-4", "op-3");

		// Cursor from last row of page 1 → next page starts AFTER op-3.
		var lastOfPage1 = page1[^1];
		var lastStartNs = lastOfPage1.StartTime.ToUnixTimeMilliseconds() * 1_000_000L;
		var page2 = await _store.ListRecentTracesAsync(Ws,
			new TracesQuery(PageSize: 2, CursorStartUnixNs: lastStartNs, CursorTraceId: lastOfPage1.TraceId),
			CancellationToken.None);
		page2.Select(t => t.RootName).Should().ContainInOrder("op-2", "op-1");
	}

	[Fact]
	public async Task ListRecentTracesAsync_EmptyStore_ReturnsEmpty()
	{
		var traces = await _store.ListRecentTracesAsync(Ws, new TracesQuery(), CancellationToken.None);
		traces.Should().BeEmpty();
	}

	[Fact]
	public async Task ListRecentTracesAsync_KqlFilter_Narrows_Result()
	{
		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("a0000000aaaaaaaa", "trace-a".PadRight(32, 'f'), t0, name: "GET /users"),
				MakeSpan("b0000000bbbbbbbb", "trace-b".PadRight(32, 'f'), t0.AddSeconds(1), name: "POST /login"),
				MakeSpan("c0000000cccccccc", "trace-c".PadRight(32, 'f'), t0.AddSeconds(2), name: "GET /error"),
			], CancellationToken.None);

		// Filter: `spans | where Name contains 'error'` — only trace-c should match.
		var filter = Kusto.Language.KustoCode.Parse("spans | where Name contains 'error'");
		var traces = await _store.ListRecentTracesAsync(Ws,
			new TracesQuery(Filter: filter), CancellationToken.None);

		traces.Should().HaveCount(1);
		traces[0].RootName.Should().Be("GET /error");
	}

	[Fact]
	public async Task ListRecentTracesAsync_SinceStartUnixNs_Returns_Only_Newer()
	{
		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);
		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("o0000000oldoldol", "trace-old".PadRight(32, 'f'), t0, name: "old"),
			], CancellationToken.None);

		var oldStartNs = t0.ToUnixTimeMilliseconds() * 1_000_000L;

		// Poll-flow: client just rendered `trace-old` at the top (its StartUnixNs = oldStartNs).
		// Now two new traces arrive. The poll should return only the two newer.
		await _store.AppendBatchAsync(Ws,
			[
				MakeSpan("n0000000newer100", "trace-new1".PadRight(32, 'f'), t0.AddSeconds(5), name: "new1"),
				MakeSpan("n0000000newer200", "trace-new2".PadRight(32, 'f'), t0.AddSeconds(10), name: "new2"),
			], CancellationToken.None);

		var newer = await _store.ListRecentTracesAsync(Ws,
			new TracesQuery(SinceStartUnixNs: oldStartNs), CancellationToken.None);

		newer.Select(t => t.RootName).Should().BeEquivalentTo(["new1", "new2"]);
		// Newest first — order preserved.
		newer[0].RootName.Should().Be("new2");
	}
}
