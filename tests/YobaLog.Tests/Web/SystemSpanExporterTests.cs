using System.Collections.Immutable;
using System.Diagnostics;
using Kusto.Language;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage;
using YobaLog.Core.Tracing;
using YobaLog.Web.Observability;

namespace YobaLog.Tests.Web;

// Unit tests for SystemSpanExporter.ToSpan (pure Activity → Span mapping) and Export
// (writes the batch into $system via ISpanStore).
//
// Activity construction requires an ActivityListener subscribed to the source — otherwise
// StartActivity returns null (zero-cost path). Tests set one up locally and tear it down.
public sealed class SystemSpanExporterTests : IDisposable
{
	readonly ActivitySource _source = new("YobaLog.Tests.SpanExporter");
	readonly ActivityListener _listener;

	public SystemSpanExporterTests()
	{
		_listener = new ActivityListener
		{
			ShouldListenTo = s => s.Name == "YobaLog.Tests.SpanExporter",
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
		};
		ActivitySource.AddActivityListener(_listener);
	}

	public void Dispose()
	{
		_listener.Dispose();
		_source.Dispose();
	}

	Activity MakeActivity(string name, Action<Activity>? configure = null)
	{
		using var activity = _source.StartActivity(name)
			?? throw new InvalidOperationException("ActivityListener failed to produce an Activity");
		configure?.Invoke(activity);
		activity.Stop();
		return activity;
	}

	[Fact]
	public void ToSpan_Sets_Name_From_DisplayName()
	{
		var activity = MakeActivity("storage.append.batch");
		var s = SystemSpanExporter.ToSpan(activity);
		s.Name.Should().Be("storage.append.batch");
	}

	[Fact]
	public void ToSpan_Preserves_TraceId_And_SpanId_AsHex()
	{
		var activity = MakeActivity("test.op");
		var s = SystemSpanExporter.ToSpan(activity);
		// Activity API guarantees 32/16 lowercase-hex strings.
		s.TraceId.Should().NotBeNullOrEmpty().And.HaveLength(32);
		s.SpanId.Should().NotBeNullOrEmpty().And.HaveLength(16);
		s.TraceId.Should().MatchRegex("^[0-9a-f]{32}$");
		s.SpanId.Should().MatchRegex("^[0-9a-f]{16}$");
	}

	[Fact]
	public void ToSpan_Records_StartTime_And_Duration()
	{
		var activity = MakeActivity("test.op");
		var s = SystemSpanExporter.ToSpan(activity);
		s.StartTime.Should().BeAfter(DateTimeOffset.UnixEpoch);
		s.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
	}

	[Fact]
	public void ToSpan_Flattens_TagObjects_Into_Attributes()
	{
		var activity = MakeActivity("test.op", a =>
		{
			a.SetTag("workspace", "my-ws");
			a.SetTag("batch.size", 42);
			a.SetTag("flag", true);
		});
		var s = SystemSpanExporter.ToSpan(activity);
		s.Attributes["workspace"].GetString().Should().Be("my-ws");
		s.Attributes["batch.size"].GetInt64().Should().Be(42);
		s.Attributes["flag"].GetBoolean().Should().BeTrue();
	}

	[Fact]
	public void ToSpan_Skips_Source_Tag_Collision()
	{
		// A tag named "source" would shadow our ActivitySource-name augmentation.
		var activity = MakeActivity("test.op", a => a.SetTag("source", "oops-user-override"));
		var s = SystemSpanExporter.ToSpan(activity);
		s.Attributes["source"].GetString().Should().Be("YobaLog.Tests.SpanExporter");
	}

	[Fact]
	public void ToSpan_Records_Status_When_Set()
	{
		var activity = MakeActivity("failing.op", a => a.SetStatus(ActivityStatusCode.Error, "boom"));
		var s = SystemSpanExporter.ToSpan(activity);
		s.Status.Should().Be(SpanStatusCode.Error);
		s.StatusDescription.Should().Be("boom");
	}

	[Fact]
	public void ToSpan_Omits_Status_When_Unset()
	{
		var activity = MakeActivity("test.op");
		var s = SystemSpanExporter.ToSpan(activity);
		s.Status.Should().Be(SpanStatusCode.Unset);
		s.StatusDescription.Should().BeNull();
	}

	[Fact]
	public void ToSpan_Sets_Source_Attribute_From_ActivitySource_Name()
	{
		var activity = MakeActivity("test.op");
		var s = SystemSpanExporter.ToSpan(activity);
		s.Attributes["source"].GetString().Should().Be("YobaLog.Tests.SpanExporter");
	}

	[Fact]
	public void ToSpan_Maps_ActivityKind_To_SpanKind()
	{
		var activity = MakeActivity("test.op");
		// Default ActivityKind is Internal.
		var s = SystemSpanExporter.ToSpan(activity);
		s.Kind.Should().Be(SpanKind.Internal);
	}

	[Fact]
	public void Export_Writes_Batch_Into_SystemWorkspace()
	{
		var a1 = MakeActivity("op.one");
		var a2 = MakeActivity("op.two");

		var store = new CapturingSpanStore();
		var exporter = new SystemSpanExporter(store, NullLogger<SystemSpanExporter>.Instance);
		var batch = new Batch<Activity>([a1, a2], count: 2);

		var result = exporter.Export(batch);
		result.Should().Be(ExportResult.Success);

		store.Writes.Should().HaveCount(1);
		var (ws, spans) = store.Writes[0];
		ws.Should().Be(WorkspaceId.System);
		spans.Should().HaveCount(2);
		spans.Select(s => s.Name).Should().BeEquivalentTo(["op.one", "op.two"]);
	}

	[Fact]
	public void Export_EmptyBatch_Returns_Success_Without_Writing()
	{
		var store = new CapturingSpanStore();
		var exporter = new SystemSpanExporter(store, NullLogger<SystemSpanExporter>.Instance);
		var result = exporter.Export(new Batch<Activity>([], count: 0));
		result.Should().Be(ExportResult.Success);
		store.Writes.Should().BeEmpty();
	}

	sealed class CapturingSpanStore : ISpanStore
	{
		public List<(WorkspaceId Ws, List<Span> Spans)> Writes { get; } = [];

		public ValueTask AppendBatchAsync(WorkspaceId workspaceId, IReadOnlyList<Span> batch, CancellationToken ct)
		{
			Writes.Add((workspaceId, [.. batch]));
			return ValueTask.CompletedTask;
		}

		public ValueTask InitializeAsync(CancellationToken ct) => ValueTask.CompletedTask;
		public ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct) => ValueTask.CompletedTask;
		public ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct) => ValueTask.CompletedTask;
		public ValueTask<IReadOnlyList<Span>> GetByTraceIdAsync(WorkspaceId workspaceId, string traceId, CancellationToken ct) =>
			new(Array.Empty<Span>());
		public IAsyncEnumerable<Span> QueryKqlAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct) =>
			EmptyAsync();
		public Task<KqlResult> QueryKqlResultAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct) =>
			Task.FromException<KqlResult>(new NotSupportedException());
		public ValueTask<long> CountAsync(WorkspaceId workspaceId, CancellationToken ct) => new(0L);
		public ValueTask<long> DeleteOlderThanAsync(WorkspaceId workspaceId, DateTimeOffset cutoff, CancellationToken ct) =>
			new(0L);

		static async IAsyncEnumerable<Span> EmptyAsync()
		{
			await ValueTask.CompletedTask;
			yield break;
		}
	}
}
