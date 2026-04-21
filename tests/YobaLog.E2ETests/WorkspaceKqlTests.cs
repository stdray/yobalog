using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class WorkspaceKqlTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	public WorkspaceKqlTests(WebAppFixture app) => _app = app;

	public async Task InitializeAsync()
	{
		// Auth cookie comes from the fixture's storage state — no per-test login round-trip.
		_ctx = await _app.NewContextAsync();
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) await _ctx.CloseAsync();
	}

	[Fact]
	public async Task Filter_by_level_returns_only_matching_rows()
	{
		var now = DateTimeOffset.UtcNow;
		await _app.SeedAsync("demo",
			Event(LogLevel.Error, "boom", now.AddSeconds(-3)),
			Event(LogLevel.Information, "hello", now.AddSeconds(-2)),
			Event(LogLevel.Error, "crash", now.AddSeconds(-1)));

		var ws = new WorkspacePage(_page!);
		await ws.GotoAsync("demo");
		await ws.AssertRowCountAsync(3);

		// Level column is int rank 0..5 (Verbose=0 … Error=4 … Fatal=5); symbolic 'Error' isn't
		// a KQL-resolvable identifier in our schema. Either numeric rank or LevelName works.
		await ws.SubmitKqlAsync("events | where Level >= 4");

		await ws.AssertRowCountAsync(2);
		await ws.AssertMessagesAsync("boom", "crash");
	}

	[Fact]
	public async Task Kql_syntax_error_renders_alert()
	{
		var ws = new WorkspacePage(_page!);
		await ws.GotoAsync("demo");

		await ws.SubmitKqlAsync("events | whre Level >= 4");

		await ws.AssertKqlErrorContainsAsync("parse error");
	}
}
