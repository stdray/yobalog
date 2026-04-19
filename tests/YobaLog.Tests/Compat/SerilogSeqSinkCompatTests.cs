using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using YobaLog.Core.Storage;
using LogEvent = YobaLog.Core.LogEvent;

namespace YobaLog.Tests.Compat;

/// <summary>
/// End-to-end check that Serilog + Serilog.Sinks.Seq (the canonical Seq client in the .NET world —
/// Seq.Extensions.Logging is a thin wrapper on top) reaches /compat/seq/api/events/raw cleanly,
/// the CLEF lines it emits parse, and the resulting events land in storage with the expected fields.
/// </summary>
public sealed class SerilogSeqSinkCompatTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-serilog");

	public SerilogSeqSinkCompatTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-serilogcompat-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["SqliteLogStore:DataDirectory"] = _tempDir,
						["ApiKeys:Keys:0:Token"] = "compat-serilog-key",
						["ApiKeys:Keys:0:Workspace"] = Ws.Value,
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		_ = _factory.Services;
		_ = _factory.CreateClient();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	[Fact]
	public async Task Serilog_SeqSink_DeliversStructuredEventsThroughRawEndpoint()
	{
		// Serilog.Sinks.Seq exposes a messageHandler parameter — we feed it TestServer's in-memory
		// handler so no real port is needed. The library still emits proper CLEF over HTTP.
		var handler = _factory.Server.CreateHandler();

		var logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Seq(
				serverUrl: _factory.Server.BaseAddress + "compat/seq",
				apiKey: "compat-serilog-key",
				messageHandler: handler,
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

		var store = (ILogStore)_factory.Services.GetRequiredService(typeof(ILogStore));
		var events = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			events.Add(e);

		events.Should().HaveCount(3);

		// Levels round-trip (Serilog Information/Warning/Error map to our LogLevel).
		events.Select(e => e.Level).Should()
			.Contain(LogLevel.Information)
			.And.Contain(LogLevel.Warning)
			.And.Contain(LogLevel.Error);

		// Templates are preserved (Serilog sends @mt, our parser falls back to template when @m is absent).
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
		var handler = _factory.Server.CreateHandler();
		var logger = new LoggerConfiguration()
			.WriteTo.Seq(
				serverUrl: _factory.Server.BaseAddress + "compat/seq",
				apiKey: "wrong-key",
				messageHandler: handler,
				batchPostingLimit: 1,
				period: TimeSpan.FromMilliseconds(50))
			.CreateLogger();

		logger.Information("should be rejected");
		await logger.DisposeAsync();
		await Task.Delay(300);

		var store = (ILogStore)_factory.Services.GetRequiredService(typeof(ILogStore));
		var count = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		count.Should().Be(0);
	}

	async Task WaitForEventsAsync(long expected)
	{
		var store = (ILogStore)_factory.Services.GetRequiredService(typeof(ILogStore));
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
