using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using YobaLog.Core.Tracing;
using SpanKind = YobaLog.Core.Tracing.SpanKind;
using SpanStatusCode = YobaLog.Core.Tracing.SpanStatusCode;

namespace YobaLog.E2ETests.Compat;

// End-to-end: OpenTelemetry .NET SDK tracer provider → OTLP HTTP/Protobuf exporter →
// /v1/traces → our ISpanStore. Exercises the full wire path against a real KestrelAppHost,
// catching SDK-side quirks the unit-level parser tests can't see (resource-attr defaults,
// Activity → OTLP conversion, etc.).
public sealed class OtlpDotnetTracesCompatTests : IAsyncLifetime
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-otlp-traces");
	readonly KestrelAppHost _host = new();

	public async Task InitializeAsync() =>
		await _host.StartAsync(s =>
		{
			s["ApiKeys:Keys:0:Token"] = "compat-otlp-traces-key";
			s["ApiKeys:Keys:0:Workspace"] = Ws.Value;
		});

	public async Task DisposeAsync() => await _host.DisposeAsync();

	[Fact]
	public async Task OtlpDotnetSdk_DeliversSpansToV1TracesEndpoint()
	{
		var source = new System.Diagnostics.ActivitySource("compat-traces-test");

		using var tracerProvider = Sdk.CreateTracerProviderBuilder()
			.AddSource("compat-traces-test")
			.SetResourceBuilder(ResourceBuilder.CreateEmpty()
				.AddService(serviceName: "otlp-dotnet-traces", serviceVersion: "0.1.0"))
			.AddOtlpExporter(otlp =>
			{
				otlp.Endpoint = new Uri(_host.BaseUrl.TrimEnd('/') + "/v1/traces");
				otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
				otlp.Headers = "X-Seq-ApiKey=compat-otlp-traces-key";
				// Simple processor for test determinism — Dispose flushes synchronously.
				otlp.ExportProcessorType = ExportProcessorType.Simple;
			})
			.Build();

		string? expectedTraceId;
		using (var parent = source.StartActivity("root.op", System.Diagnostics.ActivityKind.Server))
		{
			parent!.SetTag("user.id", "42");
			expectedTraceId = parent.TraceId.ToHexString();

			using (var childOk = source.StartActivity("child.db.query", System.Diagnostics.ActivityKind.Client))
			{
				childOk!.SetTag("db.system", "sqlite");
				childOk.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
			}

			using (var childErr = source.StartActivity("child.failing", System.Diagnostics.ActivityKind.Internal))
			{
				childErr!.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "intentional test failure");
			}
		}

		// Flushes SimpleActivityExportProcessor → POST /v1/traces → ISpanStore.AppendBatchAsync.
		tracerProvider.Dispose();

		var spansStore = _host.Services.GetRequiredService<ISpanStore>();
		await WaitForSpansAsync(spansStore, expected: 3);

		var spans = await spansStore.GetByTraceIdAsync(Ws, expectedTraceId!, CancellationToken.None);
		spans.Should().HaveCount(3, "3 activities → 3 spans landed in workspace traces.db");

		// Parent / child structure preserved.
		var root = spans.Single(s => s.Name == "root.op");
		root.ParentSpanId.Should().BeNull();
		root.Kind.Should().Be(SpanKind.Server);

		var child = spans.Single(s => s.Name == "child.db.query");
		child.ParentSpanId.Should().Be(root.SpanId);
		child.Kind.Should().Be(SpanKind.Client);
		child.Attributes.Should().ContainKey("db.system");
		child.Attributes["db.system"].GetString().Should().Be("sqlite");
		child.Status.Should().Be(SpanStatusCode.Ok);

		var failing = spans.Single(s => s.Name == "child.failing");
		failing.Status.Should().Be(SpanStatusCode.Error);
		failing.StatusDescription.Should().Be("intentional test failure");

		// Resource attributes propagate onto every span (resource-wins-on-collision).
		spans.Should().AllSatisfy(s =>
		{
			s.Attributes.Should().ContainKey("service.name");
			s.Attributes["service.name"].GetString().Should().Be("otlp-dotnet-traces");
		});
	}

	[Fact]
	public async Task OtlpDotnetSdk_WrongApiKey_NoSpansLanded()
	{
		var source = new System.Diagnostics.ActivitySource("compat-traces-reject");

		using var tracerProvider = Sdk.CreateTracerProviderBuilder()
			.AddSource("compat-traces-reject")
			.AddOtlpExporter(otlp =>
			{
				otlp.Endpoint = new Uri(_host.BaseUrl.TrimEnd('/') + "/v1/traces");
				otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
				otlp.Headers = "X-Seq-ApiKey=wrong-key";
				otlp.ExportProcessorType = ExportProcessorType.Simple;
			})
			.Build();

		using (source.StartActivity("should.be.rejected")) { }
		tracerProvider.Dispose();

		await Task.Delay(300);

		var spansStore = _host.Services.GetRequiredService<ISpanStore>();
		var count = await spansStore.CountAsync(Ws, CancellationToken.None);
		count.Should().Be(0);
	}

	static async Task WaitForSpansAsync(ISpanStore store, long expected)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var c = await store.CountAsync(Ws, CancellationToken.None);
			if (c >= expected) return;
			await Task.Delay(50);
		}
		var final = await store.CountAsync(Ws, CancellationToken.None);
		throw new TimeoutException($"expected {expected} spans, got {final}");
	}
}
