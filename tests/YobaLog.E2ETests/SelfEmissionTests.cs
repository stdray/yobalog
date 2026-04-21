using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using YobaLog.Core.Storage;
using YobaLog.Web.Observability;
using LogEvent = YobaLog.Core.LogEvent;

namespace YobaLog.E2ETests;

// End-to-end Phase G self-emission: ingesting events into a user workspace should produce
// spans on the YobaLog.Ingestion / YobaLog.Storage.Sqlite sources; feeding them through
// SystemSpanExporter should land span-events in $system with Properties.Kind="span".
//
// Decision-log 2026-04-21 Rule 4: KestrelAppHost runs under Testing env (OTel wiring off),
// tests opt in via local ActivityListener that routes completed Activities through the
// exporter. That exercises real instrumentation call sites + real exporter without
// overriding the env gate.
public sealed class SelfEmissionTests : IAsyncLifetime
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("self-emit-ws");
	readonly KestrelAppHost _host = new();
	ActivityListener? _listener;

	public async Task InitializeAsync()
	{
		await _host.StartAsync(s =>
		{
			s["ApiKeys:Keys:0:Token"] = "self-emit-key";
			s["ApiKeys:Keys:0:Workspace"] = Ws.Value;
		});

		// Wire a local ActivityListener that mimics what AddOpenTelemetry().WithTracing would
		// do: for every completed YobaLog.* Activity, feed it into SystemSpanExporter.
		var store = _host.Services.GetRequiredService<ILogStore>();
		var exporter = new SystemSpanExporter(store, NullLogger<SystemSpanExporter>.Instance);

		_listener = new ActivityListener
		{
			ShouldListenTo = s => s.Name.StartsWith("YobaLog.", StringComparison.Ordinal),
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
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
	public async Task Ingesting_Events_Produces_SpanEvents_In_SystemWorkspace()
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
		// time to run + emit its span.
		await WaitForSystemSpansAsync(atLeast: 2);

		var store = _host.Services.GetRequiredService<ILogStore>();
		var spans = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(WorkspaceId.System, new LogQuery(PageSize: 50), CancellationToken.None))
		{
			if (e.Properties.TryGetValue("Kind", out var kind) && kind.GetString() == "span")
				spans.Add(e);
		}

		spans.Should().NotBeEmpty("ingesting events must trigger at least one YobaLog.* span → exporter → $system write");

		// Expect at minimum: one YobaLog.Ingestion span (IngestAsync) + one YobaLog.Storage.Sqlite
		// span (AppendBatchAsync). The drain happens on a background thread so ordering isn't
		// guaranteed here; just check both sources appeared.
		var sources = spans.Select(s => s.Properties["Source"].GetString()).ToHashSet(StringComparer.Ordinal);
		sources.Should().Contain("YobaLog.Ingestion");
		sources.Should().Contain("YobaLog.Storage.Sqlite");

		// Span-shape sanity: each span-event must carry the flattened fields SystemSpanExporter
		// documents (Kind / Name / StartUnixNs / DurationNs).
		spans.Should().AllSatisfy(s =>
		{
			s.Properties.Should().ContainKey("Name");
			s.Properties.Should().ContainKey("StartUnixNs");
			s.Properties.Should().ContainKey("DurationNs");
			s.TraceId.Should().NotBeNullOrEmpty();
			s.SpanId.Should().NotBeNullOrEmpty();
		});
	}

	[Fact]
	public async Task System_Workspace_Writes_Are_Not_Instrumented()
	{
		// Defence in depth: even though ActivityListener would try to export a span if one was
		// created for a $system write, the instrumented code skips StartActivity when
		// workspaceId.IsSystem. Otherwise every span export would recurse: span → AppendBatch
		// → span → AppendBatch → ... until ChannelIngestionPipeline drops.
		//
		// To assert: write directly to $system via the store; no new span-events should appear.
		var store = _host.Services.GetRequiredService<ILogStore>();
		await store.AppendBatchAsync(WorkspaceId.System, new[]
		{
			new YobaLog.Core.LogEventCandidate(
				DateTimeOffset.UtcNow,
				YobaLog.Core.LogLevel.Information,
				"system-write",
				"system-write",
				null, null, null, null,
				System.Collections.Immutable.ImmutableDictionary<string, System.Text.Json.JsonElement>.Empty),
		}, CancellationToken.None);

		// Let any background spans settle.
		await Task.Delay(200);

		var beforeCount = await store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);

		// Second $system write — if we were instrumenting $system, ActivityListener would fire,
		// exporter would write, count would jump by >1 (new event + span for the append).
		await store.AppendBatchAsync(WorkspaceId.System, new[]
		{
			new YobaLog.Core.LogEventCandidate(
				DateTimeOffset.UtcNow,
				YobaLog.Core.LogLevel.Information,
				"system-write-2",
				"system-write-2",
				null, null, null, null,
				System.Collections.Immutable.ImmutableDictionary<string, System.Text.Json.JsonElement>.Empty),
		}, CancellationToken.None);

		await Task.Delay(200);

		var afterCount = await store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
		(afterCount - beforeCount).Should().Be(1, "exactly the explicit write should appear — no instrumentation span");
	}

	async Task WaitForSystemSpansAsync(int atLeast)
	{
		var store = _host.Services.GetRequiredService<ILogStore>();
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var count = await store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
			if (count >= atLeast) return;
			await Task.Delay(50);
		}
	}
}
