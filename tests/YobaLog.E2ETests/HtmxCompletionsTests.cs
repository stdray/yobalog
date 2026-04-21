namespace YobaLog.E2ETests;

// Client-side htmx keyup trigger: typing in #kql-textarea fires a debounced GET
// /api/kql/completions → returned HTML-fragment is swapped into #kql-completions →
// suggestion buttons appear. Verifies the wiring end-to-end (schema is registered per the
// Kusto.Language GlobalState so Lev→Level/LevelName comes back without seeding anything).
[Collection(nameof(UiCollection))]
public sealed class HtmxCompletionsTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public HtmxCompletionsTests(WebAppFixture app, ITestOutputHelper output)
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
	public async Task Typing_Lev_into_kql_textarea_suggests_Level_and_LevelName()
	{
		await _page!.GotoAsync("/ws/demo");
		var textarea = _page.GetByTestId("kql-input");
		// Pre-fill so the caret sits in a column-expecting position (after `where`), then type
		// "Lev" with real keyboard events — hx-trigger is `keyup changed delay:250ms`, FillAsync
		// alone doesn't fire keyup. Typing "Lev" at pos 0 suggests tables (eventsTable), not cols.
		await textarea.FillAsync("events | where ");
		await textarea.ClickAsync();
		await _page.Keyboard.PressAsync("End");
		await textarea.PressSequentiallyAsync("Lev", new() { Delay = 30 });

		var suggestions = _page.Locator("[data-kql-completions] .kql-suggestion");
		await Expect(suggestions).Not.ToHaveCountAsync(0);
		var texts = await suggestions.AllTextContentsAsync();
		texts.Should().Contain(t => t.Contains("LevelName", StringComparison.Ordinal),
			$"LevelName should be in suggestions; actual={string.Join(" | ", texts)}");
		texts.Should().Contain(t => t == "Level" || t.StartsWith("Level", StringComparison.Ordinal),
			$"Level should be in suggestions; actual={string.Join(" | ", texts)}");
	}
}
