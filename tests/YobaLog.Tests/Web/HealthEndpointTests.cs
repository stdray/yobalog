using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Web;

namespace YobaLog.Tests.Web;

// Reproduces the /health 302 bug discovered on first prod deploy: anonymous
// curl got redirected to /Login despite `.AllowAnonymous()` on the minimal-API
// MapGet. Must use real Kestrel + Production env — WebApplicationFactory's
// TestServer short-circuits the pipeline differently and can't reproduce.
public sealed class HealthEndpointTests : IAsyncDisposable
{
	readonly string _tempDir;
	readonly WebApplication _app;
	readonly string _baseUrl;
	readonly HttpClient _client;

	public HealthEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-health-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			// Production (not Testing) — this is what the live container runs under.
			// Testing env skips UseHttpsRedirection and changes nothing else observably;
			// the 302 only surfaces under Production with real Kestrel.
			EnvironmentName = "Production",
			ApplicationName = typeof(YobaLogApp).Assembly.GetName().Name,
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["SqliteLogStore:DataDirectory"] = _tempDir,
		});
		builder.WebHost.UseKestrel();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		YobaLogApp.ConfigureServices(builder);

		_app = builder.Build();
		YobaLogApp.Configure(_app);
		_app.StartAsync().GetAwaiter().GetResult();

		_baseUrl = _app.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not report an address");

		_client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
		{
			BaseAddress = new Uri(_baseUrl),
		};
	}

	public async ValueTask DisposeAsync()
	{
		_client.Dispose();
		await _app.StopAsync();
		await _app.DisposeAsync();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	[Theory]
	[InlineData("/health")]
	[InlineData("/version")]
	public async Task Get_Anonymous_ReturnsOk(string path)
	{
		using var resp = await _client.GetAsync(path);
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			$"AllowAnonymous() on the minimal-API {path} endpoint must bypass the RequireAuthenticatedUser fallback policy");
	}

	[Theory]
	[InlineData("/health")]
	[InlineData("/version")]
	public async Task Head_Anonymous_ReturnsOk(string path)
	{
		// Regression for the first-deploy 302 bug: HEAD requests (`curl -I`, k8s httpGet
		// healthcheck with method=HEAD, uptime monitors) hit routing → endpoint=null when
		// MapGet is used (GET-only) → authz fallback policy kicks in → 302 /Login. Fix was
		// MapMethods("...", ["GET", "HEAD"], ...). This theory guards that HEAD keeps
		// returning 200 forever.
		using var req = new HttpRequestMessage(HttpMethod.Head, path);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			$"HEAD {path} must return 200 — MapMethods must include HEAD alongside GET for health probes");
	}
}
