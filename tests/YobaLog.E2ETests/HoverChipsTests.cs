using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Hover-chips on event cells: clicking ✓/✗ (or from/to on Timestamp) appends a KQL predicate
// and auto-submits. The row/cell chips are append-only — MVP by spec §4; smart-merge is a
// deferred follow-up. We test the Level column ✓ path; the Timestamp from/to and TraceId
// variants share the same JS handler so they're covered implicitly.
[Collection(nameof(UiCollection))]
public sealed class HoverChipsTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public HoverChipsTests(WebAppFixture app, ITestOutputHelper output)
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
	public async Task Click_Level_eq_chip_appends_where_predicate_and_filters()
	{
		var ws = FreshWorkspace("hover-chip");
		var now = DateTimeOffset.UtcNow;
		await _app.SeedAsync(ws,
			Event(LogLevel.Error, "boom-err", now.AddSeconds(-3)),
			Event(LogLevel.Information, "hi-info-1", now.AddSeconds(-2)),
			Event(LogLevel.Information, "hi-info-2", now.AddSeconds(-1)));

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(3);

		// Locate the Error row by its message cell, then trigger the hover-reveal on the Level
		// cell (Tailwind `.group:hover` → `.group-hover:inline-flex`, otherwise chip is
		// display:none and Playwright clicks are rejected as "not visible"). Hover the cell, then
		// click the ✓ chip. filter-chip attributes (data-filter-field/op/value) are the domain
		// contract the JS handler relies on — stable selectors without a dedicated data-testid.
		var errorRow = _page.GetByTestId("events-row")
			.Filter(new() { Has = _page.GetByTestId("event-message").Filter(new() { HasText = "boom-err" }) });
		var levelCell = errorRow.Locator("td").Filter(new() { Has = _page.Locator("[data-filter-field=Level]") });
		await levelCell.HoverAsync();
		await levelCell.Locator("[data-filter-field=Level][data-filter-op=eq]").ClickAsync();

		// Chip handler appends `| where Level == 4` and requestSubmit()s → page reloads with
		// that KQL. Only the error row survives the filter; the textarea now carries the predicate.
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(1);
		await Expect(_page.GetByTestId("event-message").Filter(new() { HasText = "boom-err" })).ToBeVisibleAsync();

		var kql = await _page.GetByTestId("kql-input").InputValueAsync();
		kql.Should().Contain("where Level == 4");
	}
}
