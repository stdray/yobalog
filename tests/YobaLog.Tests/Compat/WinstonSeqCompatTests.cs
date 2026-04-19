using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Storage;
using YobaLog.Web;
using LogEvent = YobaLog.Core.LogEvent;

namespace YobaLog.Tests.Compat;

/// <summary>
/// End-to-end: `@datalust/winston-seq` running under bun hits a real Kestrel-backed Web app on
/// an ephemeral port. Structured events land in our SQLite store. Requires `bun` on PATH
/// (CI setup-bun covers it; locally `winget install Oven.Bun` or similar).
/// </summary>
public sealed class WinstonSeqCompatTests : IAsyncLifetime
{
	static readonly string FixtureDir = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "winston-seq");
	static readonly WorkspaceId Ws = WorkspaceId.Parse("compat-winston");

	readonly string _tempDir;
	WebApplication? _app;
	string _baseUrl = "";

	public WinstonSeqCompatTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-winstoncompat-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
	}

	public async Task InitializeAsync()
	{
		// Build a real Kestrel-hosted app (not WebApplicationFactory, which hard-codes TestServer).
		var builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["SqliteLogStore:DataDirectory"] = _tempDir,
			["ApiKeys:Keys:0:Token"] = "compat-winston-key",
			["ApiKeys:Keys:0:Workspace"] = Ws.Value,
		});
		builder.WebHost.UseKestrel();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		YobaLogApp.ConfigureServices(builder);

		_app = builder.Build();
		YobaLogApp.Configure(_app);
		await _app.StartAsync();

		var server = _app.Services.GetRequiredService<IServer>();
		_baseUrl = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not report an address");
	}

	public async Task DisposeAsync()
	{
		if (_app is not null)
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	[Fact]
	public async Task WinstonSeq_UnderBun_DeliversStructuredEventsThroughRawEndpoint()
	{
		if (!BunAvailable())
			return; // Silently skip when bun isn't on PATH (local dev without bun).

		if (!Directory.Exists(Path.Combine(FixtureDir, "node_modules")))
			await RunBunAsync(FixtureDir, "install");

		var env = new Dictionary<string, string>
		{
			["SEQ_URL"] = _baseUrl.TrimEnd('/') + "/compat/seq",
			["SEQ_API_KEY"] = "compat-winston-key",
		};
		await RunBunAsync(FixtureDir, "run emit.ts", env);

		await WaitForEventsAsync(expected: 3);

		var store = _app!.Services.GetRequiredService<ILogStore>();
		var events = new List<LogEvent>();
		await foreach (var e in store.QueryAsync(Ws, new LogQuery(PageSize: 10), CancellationToken.None))
			events.Add(e);

		events.Should().HaveCount(3);
		events.Select(e => e.Level).Should()
			.Contain(LogLevel.Information).And.Contain(LogLevel.Warning).And.Contain(LogLevel.Error);

		events.Should().Contain(e => e.MessageTemplate.Contains("hello from", StringComparison.Ordinal));

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
		var outText = await stdout;
		var errText = await stderr;
		if (proc.ExitCode != 0)
			throw new InvalidOperationException(
				$"bun {args} exited with {proc.ExitCode}\nstdout: {outText}\nstderr: {errText}");
		if (!string.IsNullOrWhiteSpace(outText) || !string.IsNullOrWhiteSpace(errText))
			Console.WriteLine($"bun {args} stdout: {outText}\nstderr: {errText}");
	}

	async Task WaitForEventsAsync(long expected)
	{
		var store = _app!.Services.GetRequiredService<ILogStore>();
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
