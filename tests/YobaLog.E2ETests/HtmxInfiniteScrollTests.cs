using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Full client-side htmx behavior: unlike InfiniteScrollTests (which follows the sentinel's href
// manually as a contract check), this variant loads real htmx.min.js via Playwright route →
// scrolls the sentinel into view → the intersect trigger fires → hx-get brings back the next
// page and outerHTML-swaps in place.
[Collection(nameof(UiCollection))]
public sealed class HtmxInfiniteScrollTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public HtmxInfiniteScrollTests(WebAppFixture app, ITestOutputHelper output)
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
	public async Task Scrolling_sentinel_into_view_loads_next_page()
	{
		var ws = FreshWorkspace("htmx-scroll");
		var now = DateTimeOffset.UtcNow;
		var events = Enumerable.Range(0, 60)
			.Select(i => Event(LogLevel.Information, $"htmx-scroll-{i:D2}", now.AddSeconds(-60 + i)))
			.ToArray();
		await _app.SeedAsync(ws, events);

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(50);

		// Scroll the sentinel into the viewport → htmx intersect trigger fires hx-get → server
		// returns the remaining 10 rows as a partial + no new sentinel → outerHTML swap replaces
		// the sentinel with the new rows. Row count grows to 60.
		await _page.GetByTestId("events-sentinel").ScrollIntoViewIfNeededAsync();
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(60);
		await Expect(_page.GetByTestId("events-sentinel")).Not.ToBeAttachedAsync();
	}
}
