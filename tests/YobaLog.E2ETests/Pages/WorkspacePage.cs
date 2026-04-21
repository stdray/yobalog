namespace YobaLog.E2ETests.Pages;

public sealed class WorkspacePage(IPage page)
{
	public async Task GotoAsync(string workspace)
	{
		await page.GotoAsync($"/ws/{workspace}");
	}

	public async Task SubmitKqlAsync(string kql)
	{
		await page.GetByTestId("kql-input").FillAsync(kql);
		await page.GetByTestId("kql-apply").ClickAsync();
		// ClickAsync on a submit button auto-waits for GET navigation. Expect(...) auto-retries
		// against the reloaded page, so no explicit wait is needed.
	}

	public Task AssertRowCountAsync(int expected) =>
		Expect(page.GetByTestId("events-row")).ToHaveCountAsync(expected);

	public async Task AssertMessagesAsync(params string[] messages)
	{
		var cells = page.GetByTestId("event-message");
		foreach (var m in messages)
			await Expect(cells.Filter(new() { HasText = m })).ToBeVisibleAsync();
	}

	public Task AssertKqlErrorContainsAsync(string text) =>
		Expect(page.GetByTestId("kql-error")).ToContainTextAsync(text);

	public Task AssertNoEventsAsync() =>
		Expect(page.GetByTestId("events-empty")).ToBeVisibleAsync();
}
