using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Ingestion;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Client-side htmx-ext-sse wiring: toggling live-tail builds an sse-connect div via admin.ts,
// htmx-ext-sse opens the stream, each SSE `event: event` frame is swapped into #events-body
// via hx-swap=afterbegin. LiveTailTests covers the server side of the same endpoint.
[Collection(nameof(UiCollection))]
public sealed class HtmxLiveTailTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	public HtmxLiveTailTests(WebAppFixture app) => _app = app;

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
	public async Task Toggling_live_tail_prepends_new_event_via_SSE()
	{
		var ws = FreshWorkspace("htmx-sse");
		await _app.SeedAsync(ws, Event(LogLevel.Information, "initial-row"));

		await _page!.GotoAsync($"/ws/{ws}");
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(1);

		// Toggle live-tail → admin.ts builds the sse-connect container → htmx-ext-sse opens a
		// streaming connection to /api/ws/{ws}/tail.
		await _page.Locator("#live-tail-toggle").CheckAsync();

		// Wait for SSE subscription to register on the server side — publishing before the server
		// subscribes drops the message. 500ms is comfortably over observed ~50-200ms settle time.
		await _page.WaitForFunctionAsync("() => document.querySelector('#live-tail-sse') !== null");
		await Task.Delay(500);

		var broadcaster = _app.Services.GetRequiredService<ITailBroadcaster>();
		var wsId = WorkspaceId.Parse(ws);
		var candidate = new LogEventCandidate(
			Timestamp: DateTimeOffset.UtcNow,
			Level: LogLevel.Error,
			MessageTemplate: "htmx-live-tail-arrived",
			Message: "htmx-live-tail-arrived",
			Exception: null, TraceId: null, SpanId: null, EventId: null,
			Properties: ImmutableDictionary<string, JsonElement>.Empty);
		broadcaster.Publish(wsId, [candidate]);

		// New row prepended → count = 2; the fresh live row carries the published message.
		await Expect(_page.GetByTestId("events-row")).ToHaveCountAsync(2);
		await Expect(_page.GetByTestId("event-message").Filter(new() { HasText = "htmx-live-tail-arrived" }))
			.ToBeVisibleAsync();
	}
}
