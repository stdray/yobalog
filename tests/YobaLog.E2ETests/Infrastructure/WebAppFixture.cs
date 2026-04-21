using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using YobaLog.Core.Storage;

namespace YobaLog.E2ETests.Infrastructure;

// UI-flavoured host: the shared Kestrel bootstrap + a Playwright Chromium instance.
// One-time browser install after a fresh clone: `pwsh bin/Debug/net10.0/playwright.ps1 install chromium`.
public sealed class WebAppFixture : IAsyncLifetime
{
	public const string AdminUsername = "admin";
	public const string AdminPassword = "test";

	readonly KestrelAppHost _host = new();
	IPlaywright? _playwright;
	IBrowser? _browser;

	public string BaseUrl => _host.BaseUrl;
	public IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
	public ILogStore LogStore => _host.Services.GetRequiredService<ILogStore>();
	public IServiceProvider Services => _host.Services;

	// Pre-authenticated storage state (cookie) captured once per fixture via a single login.
	// Each test's NewAuthenticatedContextAsync() loads it, skipping the redirect+form roundtrip —
	// faster (no ~100ms per test) and eliminates the login-race flake we saw without it.
	string _storageStatePath = "";

	public async Task InitializeAsync()
	{
		await _host.StartAsync(s =>
		{
			s["Admin:Username"] = AdminUsername;
			s["Admin:Password"] = AdminPassword;
			// Seed one workspace via config api-key so WorkspaceBootstrapper creates it on start.
			s["ApiKeys:Keys:0:Token"] = "ui-test-key";
			s["ApiKeys:Keys:0:Workspace"] = "demo";
		});

		_playwright = await Playwright.CreateAsync();
		_browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
		{
			Headless = true,
		});

		// One-time login → persist cookies to disk for reuse by every test context.
		_storageStatePath = Path.Combine(Path.GetTempPath(), "yobalog-ui-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");
		await using var seedCtx = await NewContextAsync(authenticated: false, trace: false);
		var seedPage = await seedCtx.NewPageAsync();
		await seedPage.GotoAsync("/Login");
		await seedPage.GetByTestId("login-username").FillAsync(AdminUsername);
		await seedPage.GetByTestId("login-password").FillAsync(AdminPassword);
		await seedPage.GetByTestId("login-submit").ClickAsync();
		await Expect(seedPage.GetByTestId("workspace-list")).ToBeVisibleAsync();
		await seedCtx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _storageStatePath });
	}

	public Task<IBrowserContext> NewContextAsync(bool authenticated = true) =>
		NewContextAsync(authenticated, trace: true);

	// `trace: false` skips tracing startup — used by the fixture's own seed-login context, which
	// closes before any test class can consume the trace (would just be discarded work).
	public async Task<IBrowserContext> NewContextAsync(bool authenticated, bool trace)
	{
		var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions
		{
			BaseURL = BaseUrl,
			IgnoreHTTPSErrors = true,
			StorageStatePath = authenticated && !string.IsNullOrEmpty(_storageStatePath) ? _storageStatePath : null,
		});
		// `<script src="https://unpkg.com/...">` in _Layout.cshtml is blocking. Without a route
		// override, headless Chromium's DNS/CDN fetch can stall 15-30s and freeze the DOM parser
		// — the body never renders. We serve local copies out of Fixtures\htmx\ that are pinned to
		// the same versions the Layout references. Bump both files if the Layout bumps.
		await ctx.RouteAsync("**/unpkg.com/htmx.org**", route => FulfillJs(route, HtmxJsPath));
		await ctx.RouteAsync("**/unpkg.com/htmx-ext-sse**", route => FulfillJs(route, HtmxSseJsPath));
		if (trace)
			await TraceArtifact.StartAsync(ctx);
		return ctx;
	}

	static readonly string HtmxJsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "htmx", "htmx.min.js");
	static readonly string HtmxSseJsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "htmx", "htmx-ext-sse.js");

	static Task FulfillJs(IRoute route, string path) =>
		route.FulfillAsync(new RouteFulfillOptions
		{
			Status = 200,
			ContentType = "application/javascript",
			Body = File.ReadAllText(path),
		});

	public async Task DisposeAsync()
	{
		if (_browser is not null)
			await _browser.CloseAsync();
		_playwright?.Dispose();
		await _host.DisposeAsync();
		if (!string.IsNullOrEmpty(_storageStatePath) && File.Exists(_storageStatePath))
			File.Delete(_storageStatePath);
	}
}
