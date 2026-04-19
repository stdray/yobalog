using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaLog.Core.SelfLogging;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.SelfLogging;

public sealed class SystemLogFlusherTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteLogStore _store;

	public SystemLogFlusherTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-self-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteLogStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync() =>
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), CancellationToken.None);

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task EventLoggedViaILogger_LandsInSystemWorkspace()
	{
		var provider = new SystemLoggerProvider(Options.Create(new SystemLoggerOptions()));
		var flusher = new SystemLogFlusher(_store, provider);
		using var cts = new CancellationTokenSource();
		var flusherTask = flusher.StartAsync(cts.Token);

		var logger = provider.CreateLogger("YobaLog.Integration");
		logger.LogInformation("self-test {Value}", 123);

		// Poll: flusher writes to store async
		await WaitForCountAsync(expected: 1);
		await flusher.StopAsync(CancellationToken.None);

		var found = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(WorkspaceId.System, new LogQuery(PageSize: 10), CancellationToken.None))
			found.Add(e);

		found.Should().HaveCount(1);
		found[0].Message.Should().Be("self-test 123");
		found[0].MessageTemplate.Should().Be("self-test {Value}");
		found[0].Properties["SourceContext"].GetString().Should().Be("YobaLog.Integration");
	}

	[Fact]
	public async Task ManyEvents_BatchedIntoSingleStoreCalls()
	{
		var provider = new SystemLoggerProvider(Options.Create(new SystemLoggerOptions { BatchSize = 100 }));
		var flusher = new SystemLogFlusher(_store, provider);
		await flusher.StartAsync(CancellationToken.None);

		var logger = provider.CreateLogger("YobaLog.Integration");
		for (var i = 0; i < 250; i++)
			logger.LogInformation("event-{I}", i);

		await WaitForCountAsync(expected: 250);
		await flusher.StopAsync(CancellationToken.None);

		var count = await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
		count.Should().Be(250);
	}

	[Fact]
	public async Task NonYobaLogCategories_DoNotReach_System()
	{
		var provider = new SystemLoggerProvider(Options.Create(new SystemLoggerOptions()));
		var flusher = new SystemLogFlusher(_store, provider);
		await flusher.StartAsync(CancellationToken.None);

		var yoba = provider.CreateLogger("YobaLog.Test");
		var msft = provider.CreateLogger("Microsoft.AspNetCore");

		msft.LogError("not-ours");
		msft.LogInformation("also-not-ours");
		yoba.LogInformation("ours");

		await WaitForCountAsync(expected: 1);
		await flusher.StopAsync(CancellationToken.None);

		var found = new List<LogEvent>();
		await foreach (var e in _store.QueryAsync(WorkspaceId.System, new LogQuery(PageSize: 10), CancellationToken.None))
			found.Add(e);

		found.Should().HaveCount(1);
		found[0].Message.Should().Be("ours");
	}

	async Task WaitForCountAsync(long expected)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var c = await _store.CountAsync(WorkspaceId.System, new LogQuery(PageSize: 1), CancellationToken.None);
			if (c >= expected)
				return;
			await Task.Delay(25);
		}
	}
}
