using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using YobaLog.Core.Tracing;
using YobaLog.Web.Observability;

namespace YobaLog.E2ETests;

// End-to-end Phase H.1 self-emission: ingesting events into a user workspace produces
// spans on the YobaLog.Ingestion / YobaLog.Storage.Sqlite sources; feeding them through
// SystemSpanExporter lands Span records in $system.traces.db (via ISpanStore).
//
// Decision-log 2026-04-21 Rule 4: KestrelAppHost runs under Testing env (AddOpenTelemetry
// off), tests opt in via local ActivityListener that routes completed Activities through
// the exporter. Exercises real instrumentation sites + real exporter without overriding
// the env gate.
public sealed class SelfEmissionTests : IAsyncLifetime
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("self-emit-ws");
	readonly KestrelAppHost _host = new();
	readonly ConcurrentBag<string> _seenTraceIds = [];
	readonly ConcurrentBag<string> _seenSources = [];
	ActivityListener? _listener;

	public async Task InitializeAsync()
	{
		await _host.StartAsync(s =>
		{
			s["ApiKeys:Keys:0:Token"] = "self-emit-key";
			s["ApiKeys:Keys:0:Workspace"] = Ws.Value;
		});

		var spans = _host.Services.GetRequiredService<ISpanStore>();
		var exporter = new SystemSpanExporter(spans, NullLogger<SystemSpanExporter>.Instance);

		_listener = new ActivityListener
		{
			ShouldListenTo = s => s.Name.StartsWith("YobaLog.", StringComparison.Ordinal),
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				_seenTraceIds.Add(activity.TraceId.ToHexString());
				_seenSources.Add(activity.Source.Name);
				// Single-activity batch per stop — the exporter handles the direct-write path.
				exporter.Export(new Batch<Activity>([activity], count: 1));
			},
		};
		ActivitySource.AddActivityListener(_listener);
	}

	public async Task DisposeAsync()
	{
		_listener?.Dispose();
		await _host.DisposeAsync();
	}

	[Fact]
	public async Task Ingesting_Events_Produces_Spans_In_SystemTracesDb()
	{
		using var client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
		var body = """{"@t":"2026-04-21T12:00:00Z","@l":"Information","@m":"self-emission-probe"}""";
		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/clef")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		req.Headers.Add("X-Seq-ApiKey", "self-emit-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);

		// ChannelIngestionPipeline drains asynchronously — give SqliteLogStore.AppendBatchAsync
		// time to run + emit its span + exporter to land it in $system.traces.db.
		await WaitForSystemSpansAsync(atLeast: 2);

		var spansStore = _host.Services.GetRequiredService<ISpanStore>();
		var count = await spansStore.CountAsync(WorkspaceId.System, CancellationToken.None);
		count.Should().BeGreaterThanOrEqualTo(2,
			"ingest triggers at least YobaLog.Ingestion + YobaLog.Storage.Sqlite spans");

		// Both expected sources must have fired.
		var sources = _seenSources.ToHashSet(StringComparer.Ordinal);
		sources.Should().Contain("YobaLog.Ingestion");
		sources.Should().Contain("YobaLog.Storage.Sqlite");

		// Pick any observed TraceId and round-trip through the waterfall hot path.
		var sampledTraceId = _seenTraceIds.First();
		var spans = await spansStore.GetByTraceIdAsync(WorkspaceId.System, sampledTraceId, CancellationToken.None);
		spans.Should().NotBeEmpty("GetByTraceIdAsync round-trip returns what the exporter wrote");
		spans.Should().AllSatisfy(s =>
		{
			s.SpanId.Should().HaveLength(16);
			s.TraceId.Should().Be(sampledTraceId);
			s.Name.Should().NotBeNullOrEmpty();
			s.Attributes.Should().ContainKey("source");
		});
	}

	[Fact]
	public async Task System_Workspace_Writes_Are_Not_Instrumented()
	{
		// Defence in depth: $system writes skip StartActivity (storage.traces.append.batch
		// is guarded by workspaceId.IsSystem). Otherwise every span export would recurse:
		// span → AppendBatch → span → AppendBatch → ... until the queue overflows.
		var spansStore = _host.Services.GetRequiredService<ISpanStore>();
		var before = await spansStore.CountAsync(WorkspaceId.System, CancellationToken.None);

		await spansStore.AppendBatchAsync(WorkspaceId.System, new[]
		{
			new Span(
				SpanId: "0123456789abcdef",
				TraceId: "0123456789abcdef0123456789abcdef",
				ParentSpanId: null,
				Name: "direct-write",
				Kind: SpanKind.Internal,
				StartTime: DateTimeOffset.UtcNow,
				Duration: TimeSpan.FromMilliseconds(1),
				Status: SpanStatusCode.Unset,
				StatusDescription: null,
				Attributes: System.Collections.Immutable.ImmutableDictionary<string, System.Text.Json.JsonElement>.Empty,
				Events: [],
				Links: []),
		}, CancellationToken.None);

		await Task.Delay(200);

		var after = await spansStore.CountAsync(WorkspaceId.System, CancellationToken.None);
		(after - before).Should().Be(1, "exactly the explicit write should appear — no instrumentation span");
	}

	async Task WaitForSystemSpansAsync(int atLeast)
	{
		var spansStore = _host.Services.GetRequiredService<ISpanStore>();
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var count = await spansStore.CountAsync(WorkspaceId.System, CancellationToken.None);
			if (count >= atLeast) return;
			await Task.Delay(50);
		}
	}
}
