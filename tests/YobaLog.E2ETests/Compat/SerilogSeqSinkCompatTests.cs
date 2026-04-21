using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using YobaLog.Core.Storage;
using LogEvent = YobaLog.Core.LogEvent;

namespace YobaLog.E2ETests.Compat;

// End-to-end: Serilog + Serilog.Sinks.Seq (canonical .NET Seq client — Seq.Extensions.Logging is a
// thin wrapper on top) hits /compat/seq/api/events/raw, emitted CLEF parses, events land in storage
// with the expected fields. Runs against a real Kestrel port via KestrelAppHost — same bootstrap as
// WinstonSeqCompatTests and the UI suite.
public sealed class SerilogSeqSinkCompatTests : IAsyncLifetime
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-serilog");
	readonly KestrelAppHost _host = new();

	public async Task InitializeAsync() =>
		await _host.StartAsync(s =>
		{
			s["ApiKeys:Keys:0:Token"] = "compat-serilog-key";
			s["ApiKeys:Keys:0:Workspace"] = Ws.Value;
		});

	public async Task DisposeAsync() => await _host.DisposeAsync();

	[Fact]
	public async Task Serilog_SeqSink_DeliversStructuredEventsThroughRawEndpoint()
	{
		var logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Seq(
				serverUrl: _host.BaseUrl.TrimEnd('/') + "/compat/seq",
				apiKey: "compat-serilog-key",
				restrictedToMinimumLevel: LogEventLevel.Verbose,
				batchPostingLimit: 10,
				period: TimeSpan.FromMilliseconds(50))
			.CreateLogger();

		try
		{
			logger.Information("hello from {Source} attempt {Attempt}", "serilog-compat", 1);
			logger.Warning("disk {Device} at {Percent:P0}", "/dev/sda1", 0.92);
			logger.Error(new InvalidOperationException("boom"), "explosion code {Code}", 42);
		}
		finally
		{
			await logger.DisposeAsync();
		}

		await WaitForEventsAsync(expected: 3);

		var store = _host.Services.GetRequiredService<ILogStore>();
		var events = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			events.Add(e);

		events.Should().HaveCount(3);

		// Levels round-trip (Serilog Information/Warning/Error map to our LogLevel).
		events.Select(e => e.Level).Should()
			.Contain(LogLevel.Information)
			.And.Contain(LogLevel.Warning)
			.And.Contain(LogLevel.Error);

		// Templates preserved (Serilog sends @mt; parser falls back to template when @m absent).
		events.Should().Contain(e => e.MessageTemplate.Contains("hello from", StringComparison.Ordinal));
		events.Should().Contain(e => e.MessageTemplate.Contains("disk", StringComparison.Ordinal));

		// Exception flows through @x.
		var errorEvent = events.Single(e => e.Level == LogLevel.Error);
		errorEvent.Exception.Should().NotBeNull();
		errorEvent.Exception!.Should().Contain("InvalidOperationException");
		errorEvent.Exception.Should().Contain("boom");

		// Structured properties land in the dynamic bag.
		var infoEvent = events.Single(e => e.Level == LogLevel.Information);
		infoEvent.Properties.Should().ContainKey("Source");
		infoEvent.Properties["Source"].GetString().Should().Be("serilog-compat");
		infoEvent.Properties.Should().ContainKey("Attempt");
		infoEvent.Properties["Attempt"].GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task Serilog_SeqSink_WrongApiKey_NoEventsLanded()
	{
		var logger = new LoggerConfiguration()
			.WriteTo.Seq(
				serverUrl: _host.BaseUrl.TrimEnd('/') + "/compat/seq",
				apiKey: "wrong-key",
				batchPostingLimit: 1,
				period: TimeSpan.FromMilliseconds(50))
			.CreateLogger();

		logger.Information("should be rejected");
		await logger.DisposeAsync();
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
