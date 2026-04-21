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
		await using var seedCtx = await NewContextAsync(authenticated: false);
		var seedPage = await seedCtx.NewPageAsync();
		await seedPage.GotoAsync("/Login");
		await seedPage.GetByTestId("login-username").FillAsync(AdminUsername);
		await seedPage.GetByTestId("login-password").FillAsync(AdminPassword);
		await seedPage.GetByTestId("login-submit").ClickAsync();
		await Expect(seedPage.GetByTestId("workspace-list")).ToBeVisibleAsync();
		await seedCtx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _storageStatePath });
	}

	public async Task<IBrowserContext> NewContextAsync(bool authenticated = true)
	{
		var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions
		{
			BaseURL = BaseUrl,
			IgnoreHTTPSErrors = true,
			StorageStatePath = authenticated && !string.IsNullOrEmpty(_storageStatePath) ? _storageStatePath : null,
		});
		// Block external CDN fetches (htmx from unpkg.com). `<script src="https://unpkg.com/...">`
		// is a blocking tag and our tests were hanging DOM parsing on flaky DNS/CDN latency inside
		// headless Chromium (classic symptom: page stuck on first-script, body never appears).
		// The tests exercise server-side rendering + basic DOM, not htmx client behavior, so empty
		// responses are fine. When live-tail/htmx tests land, flip specific routes to serve local
		// copies of htmx instead.
		await ctx.RouteAsync("**/unpkg.com/**", route => route.FulfillAsync(new RouteFulfillOptions
		{
			Status = 200,
			ContentType = "application/javascript",
			Body = "",
		}));
		return ctx;
	}

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
