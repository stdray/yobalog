using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage;
using YobaLog.Web.Observability;

namespace YobaLog.Tests.Web;

// Unit tests for SystemSpanExporter.ToCandidate (pure Activity → LogEventCandidate mapping)
// and Export (writes the batch into $system via ILogStore).
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
	public void ToCandidate_Sets_Kind_Span_Sentinel()
	{
		var activity = MakeActivity("test.op");
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties.Should().ContainKey("Kind");
		c.Properties["Kind"].GetString().Should().Be("span");
	}

	[Fact]
	public void ToCandidate_Puts_DisplayName_Into_Message_And_Template()
	{
		var activity = MakeActivity("storage.append.batch");
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Message.Should().Be("storage.append.batch");
		c.MessageTemplate.Should().Be("storage.append.batch");
		c.Properties["Name"].GetString().Should().Be("storage.append.batch");
	}

	[Fact]
	public void ToCandidate_Preserves_TraceId_And_SpanId_AsHex()
	{
		var activity = MakeActivity("test.op");
		var c = SystemSpanExporter.ToCandidate(activity);
		// Activity API guarantees 32/16 lowercase-hex strings.
		c.TraceId.Should().NotBeNullOrEmpty().And.HaveLength(32);
		c.SpanId.Should().NotBeNullOrEmpty().And.HaveLength(16);
		c.TraceId.Should().MatchRegex("^[0-9a-f]{32}$");
		c.SpanId.Should().MatchRegex("^[0-9a-f]{16}$");
	}

	[Fact]
	public void ToCandidate_Records_StartUnixNs_And_DurationNs()
	{
		var activity = MakeActivity("test.op");
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties.Should().ContainKey("StartUnixNs");
		c.Properties.Should().ContainKey("DurationNs");
		c.Properties["StartUnixNs"].GetInt64().Should().BeGreaterThan(0);
		c.Properties["DurationNs"].GetInt64().Should().BeGreaterThanOrEqualTo(0);
	}

	[Fact]
	public void ToCandidate_Flattens_TagObjects_Into_Properties()
	{
		var activity = MakeActivity("test.op", a =>
		{
			a.SetTag("workspace", "my-ws");
			a.SetTag("batch.size", 42);
			a.SetTag("flag", true);
		});
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties["workspace"].GetString().Should().Be("my-ws");
		c.Properties["batch.size"].GetInt64().Should().Be(42);
		c.Properties["flag"].GetBoolean().Should().BeTrue();
	}

	[Fact]
	public void ToCandidate_Skips_Tags_That_Collide_With_Reserved_Keys()
	{
		// A tag named "Kind" from instrumented code would shadow our Kind="span" sentinel —
		// downstream filters `where Properties.Kind == 'span'` would miss the record.
		var activity = MakeActivity("test.op", a => a.SetTag("Kind", "oops-user-override"));
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties["Kind"].GetString().Should().Be("span");
	}

	[Fact]
	public void ToCandidate_Records_Status_When_Set()
	{
		var activity = MakeActivity("failing.op", a => a.SetStatus(ActivityStatusCode.Error, "boom"));
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties["StatusCode"].GetString().Should().Be("Error");
		c.Properties["StatusDescription"].GetString().Should().Be("boom");
	}

	[Fact]
	public void ToCandidate_Omits_Status_When_Unset()
	{
		var activity = MakeActivity("test.op");
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties.Should().NotContainKey("StatusCode");
		c.Properties.Should().NotContainKey("StatusDescription");
	}

	[Fact]
	public void ToCandidate_Sets_Source_Property_From_ActivitySource_Name()
	{
		var activity = MakeActivity("test.op");
		var c = SystemSpanExporter.ToCandidate(activity);
		c.Properties["Source"].GetString().Should().Be("YobaLog.Tests.SpanExporter");
	}

	[Fact]
	public void Export_Writes_Batch_Into_SystemWorkspace()
	{
		var a1 = MakeActivity("op.one");
		var a2 = MakeActivity("op.two");

		var store = new CapturingLogStore();
		var exporter = new SystemSpanExporter(store, NullLogger<SystemSpanExporter>.Instance);
		var batch = new Batch<Activity>([a1, a2], count: 2);

		var result = exporter.Export(batch);
		result.Should().Be(ExportResult.Success);

		store.Writes.Should().HaveCount(1);
		var (ws, candidates) = store.Writes[0];
		ws.Should().Be(WorkspaceId.System);
		candidates.Should().HaveCount(2);
		candidates.Select(c => c.Message).Should().BeEquivalentTo(["op.one", "op.two"]);
	}

	[Fact]
	public void Export_EmptyBatch_Returns_Success_Without_Writing()
	{
		var store = new CapturingLogStore();
		var exporter = new SystemSpanExporter(store, NullLogger<SystemSpanExporter>.Instance);
		var result = exporter.Export(new Batch<Activity>([], count: 0));
		result.Should().Be(ExportResult.Success);
		store.Writes.Should().BeEmpty();
	}

	sealed class CapturingLogStore : ILogStore
	{
		public List<(WorkspaceId Ws, List<LogEventCandidate> Candidates)> Writes { get; } = [];

		public ValueTask AppendBatchAsync(WorkspaceId workspaceId, IReadOnlyList<LogEventCandidate> batch, CancellationToken ct)
		{
			Writes.Add((workspaceId, [.. batch]));
			return ValueTask.CompletedTask;
		}

		public IAsyncEnumerable<LogEvent> QueryAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct) =>
			throw new NotSupportedException();
		public IAsyncEnumerable<LogEvent> QueryKqlAsync(WorkspaceId workspaceId, Kusto.Language.KustoCode kql, CancellationToken ct) =>
			throw new NotSupportedException();
		public Task<KqlResult> QueryKqlResultAsync(WorkspaceId workspaceId, Kusto.Language.KustoCode kql, CancellationToken ct) =>
			Task.FromException<KqlResult>(new NotSupportedException());
		public Task<IReadOnlyList<string>> GetPropertyKeysAsync(WorkspaceId workspaceId, CancellationToken ct) =>
			Task.FromException<IReadOnlyList<string>>(new NotSupportedException());
		public ValueTask<long> CountAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask<long> DeleteOlderThanAsync(WorkspaceId workspaceId, DateTimeOffset cutoff, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask<long> DeleteKqlAsync(WorkspaceId workspaceId, Kusto.Language.KustoCode predicate, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask DeclareIndexAsync(WorkspaceId workspaceId, string propertyPath, IndexKind kind, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, WorkspaceSchema schema, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask CompactAsync(WorkspaceId workspaceId, CancellationToken ct) =>
			throw new NotSupportedException();
		public ValueTask<WorkspaceStats> GetStatsAsync(WorkspaceId workspaceId, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
