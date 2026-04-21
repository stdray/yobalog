using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using YobaLog.Core.Storage;
using LogEvent = YobaLog.Core.LogEvent;
using LogLevel = YobaLog.Core.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace YobaLog.E2ETests.Compat;

// End-to-end: OpenTelemetry .NET SDK (OpenTelemetry.Exporter.OpenTelemetryProtocol) → our
// /v1/logs endpoint. Mirrors SerilogSeqSinkCompatTests's shape — KestrelAppHost + real
// client SDK + assert on ILogStore state. If OTel changes wire quirks (e.g. body-as-AnyValue
// defaulting, severity mapping, resource-attrs schema), this test catches it before users do.
public sealed class OtlpDotnetCompatTests : IAsyncLifetime
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-otlp-dotnet");
	readonly KestrelAppHost _host = new();

	public async Task InitializeAsync() =>
		await _host.StartAsync(s =>
		{
			s["ApiKeys:Keys:0:Token"] = "compat-otlp-dotnet-key";
			s["ApiKeys:Keys:0:Workspace"] = Ws.Value;
		});

	public async Task DisposeAsync() => await _host.DisposeAsync();

	[Fact]
	public async Task OtlpDotnetSdk_DeliversLogsToV1LogsEndpoint()
	{
		// OTEL_EXPORTER_OTLP_LOGS_ENDPOINT equivalent — we point at /v1/logs (OTel-standard
		// path, one of the two aliases decision-log 2026-04-21 Rule 2 mandates).
		using var loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddOpenTelemetry(options =>
			{
				options.SetResourceBuilder(ResourceBuilder.CreateEmpty()
					.AddService(serviceName: "otlp-dotnet-compat", serviceVersion: "0.1.0"));
				options.AddOtlpExporter(otlp =>
				{
					otlp.Endpoint = new Uri(_host.BaseUrl.TrimEnd('/') + "/v1/logs");
					otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
					otlp.Headers = "X-Seq-ApiKey=compat-otlp-dotnet-key";
					// No batching for test determinism: each LogInformation flushes immediately on Dispose.
					otlp.ExportProcessorType = ExportProcessorType.Simple;
				});
				options.IncludeFormattedMessage = true;
				options.ParseStateValues = true;
			});
			builder.SetMinimumLevel(MsLogLevel.Trace);
		});

		var logger = loggerFactory.CreateLogger("OtlpDotnetCompatTests");
		logger.LogInformation("hello from {Source}", "otlp-dotnet");
		logger.LogWarning("disk {Device} at {Percent:P0}", "/dev/sda1", 0.92);
		logger.LogError(new InvalidOperationException("boom"), "explosion code {Code}", 42);

		// Dispose drains the SimpleLogRecordExportProcessor synchronously.
		loggerFactory.Dispose();

		await WaitForEventsAsync(expected: 3);

		var store = _host.Services.GetRequiredService<ILogStore>();
		var events = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			events.Add(e);

		events.Should().HaveCount(3);

		// OTel SeverityNumber INFO/WARN/ERROR round-trip through our 6-level ladder.
		events.Select(e => e.Level).Should()
			.Contain(LogLevel.Information)
			.And.Contain(LogLevel.Warning)
			.And.Contain(LogLevel.Error);

		// Templates round-trip via OTel EventName (new in 1.5) or fall through the body.
		events.Should().Contain(e => e.Message.Contains("hello from otlp-dotnet", StringComparison.Ordinal));
		events.Should().Contain(e => e.Message.Contains("disk", StringComparison.Ordinal));

		// Resource attributes (service.name / service.version) flattened into Properties and
		// present on every record — resource-wins-on-collision rule means this is deployment-wide.
		events.Should().AllSatisfy(e =>
		{
			e.Properties.Should().ContainKey("service.name");
			e.Properties["service.name"].GetString().Should().Be("otlp-dotnet-compat");
		});

		// Structured parameters come through as attributes → Properties.
		var info = events.Single(e => e.Level == LogLevel.Information);
		info.Properties.Should().ContainKey("Source");
		info.Properties["Source"].GetString().Should().Be("otlp-dotnet");
	}

	[Fact]
	public async Task OtlpDotnetSdk_WrongApiKey_NoEventsLanded()
	{
		using var loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddOpenTelemetry(options =>
			{
				options.AddOtlpExporter(otlp =>
				{
					otlp.Endpoint = new Uri(_host.BaseUrl.TrimEnd('/') + "/v1/logs");
					otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
					otlp.Headers = "X-Seq-ApiKey=wrong-key";
					otlp.ExportProcessorType = ExportProcessorType.Simple;
				});
			});
			builder.SetMinimumLevel(MsLogLevel.Information);
		});

		var logger = loggerFactory.CreateLogger("rejected");
		logger.LogInformation("should be rejected");
		loggerFactory.Dispose();

		// Give it a moment in case the exporter is asynchronous despite Simple.
		await Task.Delay(300);

		var store = _host.Services.GetRequiredService<ILogStore>();
		var count = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		count.Should().Be(0);
	}

	async Task WaitForEventsAsync(long expected)
	{
		var store = _host.Services.GetRequiredService<ILogStore>();
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var c = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
			if (c >= expected) return;
			await Task.Delay(50);
		}
		var final = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		throw new TimeoutException($"expected {expected} events, got {final}");
	}
}
