using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Ingestion;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Phase E usability-polish: viewport awareness for live-tail.
// When the user has scrolled past the head of the events list, live-tail accumulates
// incoming rows in a staged DocumentFragment instead of prepending (which would jump
// the historical content the user is currently reading). A "N new" badge surfaces the
// pending count; clicking it flushes staged rows to the top and scrolls home.
[Collection(nameof(UiCollection))]
public sealed class LiveTailViewportTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public LiveTailViewportTests(WebAppFixture app, ITestOutputHelper output)
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

	static LogEventCandidate Candidate(string message) => new(
		Timestamp: DateTimeOffset.UtcNow,
		Level: LogLevel.Information,
		MessageTemplate: message,
		Message: message,
		Exception: null, TraceId: null, SpanId: null, EventId: null,
		Properties: ImmutableDictionary<string, JsonElement>.Empty);

	[Fact]
	public async Task Scrolled_Down_Stages_In_Badge_Instead_Of_Prepending()
	{
		var ws = FreshWorkspace("live-tail-vp");
		// 40 seed rows — enough that the page overflows a 720p viewport and
		// scrollY > 100 threshold is reachable with a single scrollTo().
		var seeds = Enumerable.Range(0, 40)
			.Select(i => Event(LogLevel.Information, $"seed-{i:D2}",
				at: DateTimeOffset.UtcNow.AddSeconds(-i)))
			.ToArray();
		await _app.SeedAsync(ws, seeds);

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(40);

		await _page.Locator("#live-tail-toggle").CheckAsync();
		await _page.WaitForFunctionAsync("() => document.querySelector('#live-tail-sse') !== null");
		await Task.Delay(500);

		// Scroll past the 100px threshold — anywhere below the head.
		await _page.EvaluateAsync("window.scrollTo(0, 600)");
		await _page.WaitForFunctionAsync("() => window.scrollY > 100");

		var broadcaster = _app.Services.GetRequiredService<ITailBroadcaster>();
		var wsId = WorkspaceId.Parse(ws);
		broadcaster.Publish(wsId, [Candidate("staged-while-scrolled")]);

		// Badge surfaces with count=1; events-body row count DOES NOT increase —
		// the row is in the staged fragment, not the DOM table.
		await Expect(_page.GetByTestId("live-tail-badge")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("live-tail-count")).ToHaveTextAsync("1");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(40);

		// Second publish accumulates.
		broadcaster.Publish(wsId, [Candidate("staged-again")]);
		await Expect(_page.GetByTestId("live-tail-count")).ToHaveTextAsync("2");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(40);

		// Click the badge → staged rows flush to events-body, badge hides, scroll goes home.
		await _page.GetByTestId("live-tail-badge").ClickAsync();
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(42);
		await Expect(_page.GetByTestId("live-tail-badge")).ToBeHiddenAsync();
		// scroll-to-top is animated ("smooth") — allow a beat then assert.
		await _page.WaitForFunctionAsync("() => window.scrollY < 50");

		// Most-recently-published row should land on top after flush (afterbegin order).
		var firstMessage = _page.GetByTestId("event-message").First;
		await Expect(firstMessage).ToHaveTextAsync("staged-again");
	}

	[Fact]
	public async Task At_Top_Prepends_Normally_Without_Badge()
	{
		var ws = FreshWorkspace("live-tail-top");
		await _app.SeedAsync(ws, Event(LogLevel.Information, "initial"));

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(1);

		await _page.Locator("#live-tail-toggle").CheckAsync();
		await _page.WaitForFunctionAsync("() => document.querySelector('#live-tail-sse') !== null");
		await Task.Delay(500);

		// scrollY is 0 — below the threshold, so normal afterbegin prepend takes over.
		var broadcaster = _app.Services.GetRequiredService<ITailBroadcaster>();
		var wsId = WorkspaceId.Parse(ws);
		broadcaster.Publish(wsId, [Candidate("arrives-at-top")]);

		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(2);
		await Expect(_page.GetByTestId("live-tail-badge")).ToBeHiddenAsync();
	}

	[Fact]
	public async Task Live_Tail_Survives_Filter_Change()
	{
		var ws = FreshWorkspace("live-tail-reconnect");
		await _app.SeedAsync(ws,
			Event(LogLevel.Information, "info-one"),
			Event(LogLevel.Error, "error-one"));

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(2);

		// Enable live-tail (no filter yet) → #live-tail-sse attached.
		await _page.Locator("#live-tail-toggle").CheckAsync();
		await _page.WaitForFunctionAsync("() => document.querySelector('#live-tail-sse') !== null");

		// Apply a KQL filter — Apply submits the GET form with liveTail=1 riding along
		// in the hidden field, reloading /ws/{ws}?kql=…&liveTail=1 and re-establishing SSE.
		await _page.GetByTestId("kql-input").FillAsync("events | where Level >= 3");
		await _page.GetByTestId("kql-apply").ClickAsync();
		await _page.WaitForURLAsync("**/ws/*?**liveTail=1**");

		// Reloaded page auto-reconnects SSE and shows only the error row (filter applied).
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(1);
		await Expect(_page.Locator("#live-tail-toggle")).ToBeCheckedAsync();
		await _page.WaitForFunctionAsync("() => document.querySelector('#live-tail-sse') !== null");
		await Task.Delay(500);

		// Publish an event that matches the new filter — it should arrive via the reconnected stream.
		var broadcaster = _app.Services.GetRequiredService<ITailBroadcaster>();
		var wsId = WorkspaceId.Parse(ws);
		broadcaster.Publish(wsId, [
			new(Timestamp: DateTimeOffset.UtcNow, Level: LogLevel.Error,
				MessageTemplate: "error-reconnected", Message: "error-reconnected",
				Exception: null, TraceId: null, SpanId: null, EventId: null,
				Properties: ImmutableDictionary<string, JsonElement>.Empty),
		]);

		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(2);
		await Expect(_page.GetByTestId("event-message").Filter(new() { HasText = "error-reconnected" }))
			.ToBeVisibleAsync();
	}
}
