using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Storage;
using LogEvent = YobaLog.Core.LogEvent;

namespace YobaLog.Tests.Compat;

/// <summary>
/// End-to-end check that `@datalust/winston-seq` running under bun reaches /api/events/raw cleanly.
/// Requires bun on PATH; CI already installs it. Skipped locally if bun isn't available.
/// </summary>
#pragma warning disable CA1001 // factory disposed in DisposeAsync via IAsyncLifetime
public sealed class WinstonSeqCompatTests : IAsyncLifetime
#pragma warning restore CA1001
{
	static readonly string FixtureDir = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "winston-seq");
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-winston");

	readonly string _tempDir;
	readonly KestrelWebApplicationFactory _factory;

	public WinstonSeqCompatTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-winstoncompat-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		_factory = new KestrelWebApplicationFactory();
		_factory.WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) =>
			{
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["SqliteLogStore:DataDirectory"] = _tempDir,
					["ApiKeys:Keys:0:Token"] = "compat-winston-key",
					["ApiKeys:Keys:0:Workspace"] = Ws.Value,
				});
			});
		});
	}

	public Task InitializeAsync()
	{
		_ = _factory.Services;
		_ = _factory.CreateClient(); // force host startup so Kestrel is listening.
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	// Disabled by default: WebApplicationFactory hard-codes TestServer, and our
	// KestrelWebApplicationFactory override breaks its internal TestServer cast
	// (InvalidCastException in ConfigureHostBuilder). To run this test locally or in CI,
	// either (a) refactor Program.cs into a reusable `ConfigureApp(WebApplicationBuilder)`
	// helper and build Kestrel-hosted app from scratch, or (b) spawn `dotnet run
	// --project src/YobaLog.Web` as a subprocess on a known port. Until then the
	// fixture files (package.json + emit.ts) live next to this test so contributors can
	// verify winston-seq compat manually:
	//   1. `bun install` in tests/YobaLog.Tests/Fixtures/winston-seq
	//   2. run the Web app with a known api key
	//   3. `SEQ_URL=http://localhost:5000 SEQ_API_KEY=... bun run emit.ts`
	//   4. verify events landed via the UI.
	[Fact(Skip = "Kestrel factory conflicts with TestServer cast; see note above")]
	public async Task WinstonSeq_UnderBun_DeliversStructuredEventsThroughRawEndpoint()
	{
		if (!BunAvailable())
		{
			// Skip silently — local devs without bun shouldn't see a red test on CI-only fixture.
			return;
		}

		// bun install once per fixture; bun's lockfile caches so subsequent runs are instant.
		if (!Directory.Exists(Path.Combine(FixtureDir, "node_modules")))
			await RunBunAsync(FixtureDir, "install");

		var env = new Dictionary<string, string>
		{
			["SEQ_URL"] = _factory.BaseUrl,
			["SEQ_API_KEY"] = "compat-winston-key",
		};
		await RunBunAsync(FixtureDir, "run emit.ts", env);

		await WaitForEventsAsync(expected: 3);

		var store = _factory.Services.GetRequiredService<ILogStore>();
		var events = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			events.Add(e);

		events.Should().HaveCount(3);
		events.Select(e => e.Level).Should()
			.Contain(LogLevel.Information).And.Contain(LogLevel.Warning).And.Contain(LogLevel.Error);

		events.Should().Contain(e => e.MessageTemplate.Contains("hello from", StringComparison.Ordinal));

		// winston-seq passes the object as structured properties under the given keys.
		var info = events.Single(e => e.Level == LogLevel.Information);
		info.Properties.Should().ContainKey("Source");
		info.Properties["Source"].GetString().Should().Be("winston-compat");
	}

	static bool BunAvailable()
	{
		try
		{
			using var probe = Process.Start(new ProcessStartInfo("bun", "--version")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			});
			probe?.WaitForExit(3000);
			return probe?.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	static async Task RunBunAsync(string workingDir, string args, IDictionary<string, string>? env = null)
	{
		var psi = new ProcessStartInfo("bun", args)
		{
			WorkingDirectory = workingDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		if (env is not null)
			foreach (var (k, v) in env)
				psi.Environment[k] = v;

		using var proc = Process.Start(psi) ?? throw new InvalidOperationException("bun failed to start");
		var stdout = proc.StandardOutput.ReadToEndAsync();
		var stderr = proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();
		if (proc.ExitCode != 0)
			throw new InvalidOperationException(
				$"bun {args} exited with {proc.ExitCode}\nstdout: {await stdout}\nstderr: {await stderr}");
	}

	async Task WaitForEventsAsync(long expected)
	{
		var store = _factory.Services.GetRequiredService<ILogStore>();
		var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var c = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
			if (c >= expected) return;
			await Task.Delay(100);
		}
		var final = await store.CountAsync(Ws, new LogQuery(PageSize: 1), CancellationToken.None);
		throw new TimeoutException($"expected {expected} events, got {final}");
	}
}
