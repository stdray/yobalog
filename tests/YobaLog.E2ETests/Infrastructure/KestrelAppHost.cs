using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Web;

namespace YobaLog.E2ETests.Infrastructure;

// Shared Kestrel+YobaLogApp bootstrap for tests that need a real TCP port (browser-backed UI tests,
// external-process compat tests like winston-seq under bun). WebApplicationFactory hard-codes
// TestServer, so consumers that aren't HttpClient-with-in-memory-handler can't use it.
public sealed class KestrelAppHost : IAsyncDisposable
{
	WebApplication? _app;
	string _tempDir = "";

	public string BaseUrl { get; private set; } = "";
	public string DataDir => _tempDir;
	public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Host not started");

	public async Task StartAsync(Action<IDictionary<string, string?>> configure)
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-e2e-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		var settings = new Dictionary<string, string?>
		{
			["SqliteLogStore:DataDirectory"] = _tempDir,
		};
		configure(settings);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			// Testing env disables UseHttpsRedirection in YobaLogApp.Configure — headless Kestrel
			// on plain http would otherwise 307-loop every request.
			EnvironmentName = "Testing",
			// ApplicationName = YobaLog.Web so MVC/Razor Pages discovers controllers + pages in the
			// web assembly. Default entry assembly here is YobaLog.E2ETests → no endpoints →
			// authz fallback policy challenges every request and redirect-loops on /Login.
			ApplicationName = typeof(YobaLogApp).Assembly.GetName().Name,
			// Point WebRootPath at the web project's wwwroot (not the test bin) so static-files
			// middleware doesn't warn, and Playwright traces/screenshots render with real CSS.
			WebRootPath = WebProjectWwwroot(),
		});
		builder.Configuration.AddInMemoryCollection(settings);
		builder.WebHost.UseKestrel();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		YobaLogApp.ConfigureServices(builder);

		_app = builder.Build();
		YobaLogApp.Configure(_app);
		await _app.StartAsync();

		BaseUrl = _app.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not report an address");
	}

	// csproj injects AssemblyMetadataAttribute("YobaLogWebProjectDir", <abs path to src/YobaLog.Web>).
	// Fail loudly if it's missing rather than silently falling back to cwd — a missing WebRootPath
	// would come back as a confusing StaticFileMiddleware warning instead of a test failure.
	static string WebProjectWwwroot()
	{
		var attr = typeof(KestrelAppHost).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "YobaLogWebProjectDir")
			?? throw new InvalidOperationException(
				"AssemblyMetadataAttribute('YobaLogWebProjectDir') missing — check YobaLog.E2ETests.csproj.");
		return Path.Combine(attr.Value!, "wwwroot");
	}

	public async ValueTask DisposeAsync()
	{
		if (_app is not null)
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}
}
