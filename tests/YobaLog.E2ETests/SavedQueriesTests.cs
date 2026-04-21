namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class SavedQueriesTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	public SavedQueriesTests(WebAppFixture app) => _app = app;

	public async Task InitializeAsync()
	{
		_ctx = await _app.NewContextAsync();
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) await _ctx.CloseAsync();
	}

	[Fact]
	public async Task Save_then_click_chip_reloads_kql()
	{
		const string savedName = "errors-only";
		const string kql = "events | where Level >= 4";

		await _page!.GotoAsync("/ws/demo");
		// Save form uses a hidden `kql` input snapshotted at render time (no client-side sync)
		// — apply first so the page re-renders with the new KQL baked in, then save.
		await _page.GetByTestId("kql-input").FillAsync(kql);
		await _page.GetByTestId("kql-apply").ClickAsync();
		await _page.GetByTestId("saved-query-name").FillAsync(savedName);
		await _page.GetByTestId("saved-query-save").ClickAsync();

		// After save, page reloads with the saved chip present + KQL pre-filled.
		var chip = _page.Locator($"[data-testid=saved-query-chip][data-saved-name='{savedName}']");
		await Expect(chip).ToBeVisibleAsync();

		// Clear the KQL box, then click the chip → KQL restored from saved query.
		await _page.GetByTestId("kql-input").FillAsync("");
		await chip.ClickAsync();

		var reloaded = await _page.GetByTestId("kql-input").InputValueAsync();
		reloaded.Should().Be(kql);
	}
}
