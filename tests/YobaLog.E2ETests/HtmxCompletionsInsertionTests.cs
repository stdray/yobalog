using System.Collections.Immutable;
using System.Text.Json;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Insertion correctness: HtmxCompletionsTests proved the dropdown arrives populated; these
// tests click specific suggestions and assert the exact textarea content afterwards. They
// guard the three flows that were broken or fragile:
//   1. Column pick (Level) — no stray leading space before "Level".
//   2. Property namespace pick (Properties) — auto-inserts trailing dot, caret lands on the
//      dot, htmx keyup fires the next completion round, property-key dropdown appears.
//   3. Property-key pick after a manually-typed `Properties.` — key inserts flush against the
//      dot, NOT " Duration". This was the regression: admin.ts treated `.` as a word char and
//      prepended a space.
[Collection(nameof(UiCollection))]
public sealed class HtmxCompletionsInsertionTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;
	string _ws = "";

	public HtmxCompletionsInsertionTests(WebAppFixture app) => _app = app;

	public async Task InitializeAsync()
	{
		_ctx = await _app.NewContextAsync();
		_page = await _ctx.NewPageAsync();

		_ws = FreshWorkspace("complete-ins");
		// Seed one event carrying a Properties.Duration key so property-key completion has
		// something to suggest — GetPropertyKeysAsync pulls keys from stored events.
		await _app.SeedAsync(_ws,
			EventWith("with-props", props: new() { ["Duration"] = 42, ["Source"] = "probe" }));
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) await _ctx.CloseAsync();
	}

	// htmx debounces keyup by 250ms, then the server round-trips. If we click too soon the
	// panel still reflects the state from an earlier keystroke (stale data-edit-start /
	// data-edit-length → insertion applies at the wrong offsets). Wait for the panel's
	// data-edit-length to match the typed prefix length.
	async Task WaitForPanelEditLengthAsync(int expected)
	{
		await _page!.WaitForFunctionAsync(
			"expected => document.querySelector('[data-kql-completions]')?.dataset.editLength === String(expected)",
			expected);
	}

	[Fact]
	public async Task Pick_column_inserts_without_leading_space()
	{
		await _page!.GotoAsync($"/ws/{_ws}");
		var textarea = _page.GetByTestId("kql-input");
		await textarea.FillAsync("events | where ");
		await textarea.ClickAsync();
		await _page.Keyboard.PressAsync("End");
		await textarea.PressSequentiallyAsync("Lev", new() { Delay = 30 });
		await WaitForPanelEditLengthAsync(3);

		await _page.Locator("[data-kql-completions] .kql-suggestion")
			.Filter(new() { HasText = "Level" }).First.ClickAsync();

		// Kusto's AfterText for column completions is " " so the caret lands ready for the next
		// token. That's legitimate UX; just pin the exact string so drift is visible.
		(await textarea.InputValueAsync()).Should().Be("events | where Level ");
	}

	[Fact]
	public async Task Pick_Properties_appends_dot_and_triggers_key_dropdown()
	{
		await _page!.GotoAsync($"/ws/{_ws}");
		var textarea = _page.GetByTestId("kql-input");
		await textarea.FillAsync("events | where ");
		await textarea.ClickAsync();
		await _page.Keyboard.PressAsync("End");
		await textarea.PressSequentiallyAsync("Prop", new() { Delay = 30 });
		await WaitForPanelEditLengthAsync(4);

		await _page.Locator("[data-kql-completions] .kql-suggestion")
			.Filter(new() { HasText = "Properties" }).First.ClickAsync();

		// Dot auto-appended; admin.ts dispatches a keyup so the server hits the property-key
		// discovery path → `Duration` suggestion materializes without any extra typing.
		(await textarea.InputValueAsync()).Should().Be("events | where Properties.");
		await Expect(_page.Locator("[data-kql-completions] .kql-suggestion").Filter(new() { HasText = "Duration" }))
			.ToBeVisibleAsync();
	}

	[Fact]
	public async Task Pick_property_key_after_manual_dot_inserts_flush_no_leading_space()
	{
		// Regression: admin.ts's needsLeadingSpace regex was `/[\s(]/` — didn't include `.`, so
		// after `Properties.` (prevChar='.') the handler prepended a space, producing
		// "Properties. Duration" instead of "Properties.Duration".
		await _page!.GotoAsync($"/ws/{_ws}");
		var textarea = _page.GetByTestId("kql-input");
		await textarea.FillAsync("events | where ");
		await textarea.ClickAsync();
		await _page.Keyboard.PressAsync("End");
		await textarea.PressSequentiallyAsync("Properties.", new() { Delay = 30 });
		// After `.` the property-key discovery path returns editLength=0 (cursor position,
		// nothing to replace). Wait for the right panel state.
		await WaitForPanelEditLengthAsync(0);

		var keySuggestion = _page.Locator("[data-kql-completions] .kql-suggestion")
			.Filter(new() { HasText = "Duration" });
		await Expect(keySuggestion).ToBeVisibleAsync();
		await keySuggestion.First.ClickAsync();

		(await textarea.InputValueAsync()).Should().Be("events | where Properties.Duration");
	}

	static LogEventCandidate EventWith(string message, Dictionary<string, object> props)
	{
		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var (k, v) in props)
			builder[k] = JsonSerializer.SerializeToElement(v);
		return new LogEventCandidate(
			Timestamp: DateTimeOffset.UtcNow,
			Level: LogLevel.Information,
			MessageTemplate: message,
			Message: message,
			Exception: null, TraceId: null, SpanId: null, EventId: null,
			Properties: builder.ToImmutable());
	}
}
