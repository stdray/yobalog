using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class InfiniteScrollTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public InfiniteScrollTests(WebAppFixture app, ITestOutputHelper output)
	{
		_app = app;
		_output = output;
	}

	public async Task InitializeAsync()
	{
		_ctx = await _app.NewContextAsync();
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, _output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task Cursor_pagination_contract_returns_next_page_via_sentinel_href()
	{
		// Fresh workspace to avoid seed bleed from other UI tests in the same collection.
		var ws = FreshWorkspace("scroll");
		var now = DateTimeOffset.UtcNow;
		var events = Enumerable.Range(0, 60)
			.Select(i => Event(LogLevel.Information, $"scroll-evt-{i:D2}", now.AddSeconds(-60 + i)))
			.ToArray();
		await _app.SeedAsync(ws, events);

		// First page: exactly PageSize (50) rows + one sentinel row with data-next-href.
		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(50);
		var sentinel = _page.GetByTestId("events-sentinel");
		await Expect(sentinel).ToBeVisibleAsync();

		// Client-side htmx infinite scroll is blocked in tests (external CDN fulfilled empty), so
		// we verify the contract directly: follow the sentinel's hx-get URL via a second navigation.
		// Production-wise the htmx intersect trigger fires GET on that same URL with HX-Request,
		// which is covered at the server handler level — here we only prove the URL works.
		var nextHref = await sentinel.GetAttributeAsync("data-next-href")
			?? throw new InvalidOperationException("sentinel missing data-next-href");
		await _page.GotoAsync(nextHref);

		// Remaining 60-50 = 10 events on the second page.
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(10);
		await Expect(_page.GetByTestId("events-sentinel")).Not.ToBeAttachedAsync();
	}
}
