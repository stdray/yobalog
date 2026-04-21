using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Ingestion;

namespace YobaLog.E2ETests;

// SSE-based live-tail contract test. Playwright context blocks external CDN (including
// htmx-ext-sse) so client-side wiring isn't testable here — instead we verify the server
// endpoint directly: subscribe via streaming HttpClient, publish to the singleton
// ITailBroadcaster, assert the event arrives on the stream as an SSE frame.
[Collection(nameof(UiCollection))]
public sealed class LiveTailTests
{
	readonly WebAppFixture _app;

	public LiveTailTests(WebAppFixture app) => _app = app;

	[Fact]
	public async Task Broadcaster_publish_reaches_SSE_subscriber()
	{
		var ws = WorkspaceId.Parse("demo");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

		// Tail endpoint is authenticated — HttpAuthHelper handles the antiforgery-token dance.
		using var http = await HttpAuthHelper.AuthenticatedClientAsync(_app);
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/ws/{ws.Value}/tail?kql=events");
		using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
		resp.EnsureSuccessStatusCode();
		resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

		await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
		using var reader = new StreamReader(stream);

		// Broadcaster is singleton, so publishing here reaches the subscription we just opened.
		// Small delay — the SSE endpoint's Subscribe() is async and needs to register before we
		// publish, otherwise the publish fires into no subscribers and is dropped.
		await Task.Delay(200, cts.Token);

		var broadcaster = _app.Services.GetRequiredService<ITailBroadcaster>();
		var candidate = new LogEventCandidate(
			Timestamp: DateTimeOffset.UtcNow,
			Level: LogLevel.Information,
			MessageTemplate: "live-tail-hello",
			Message: "live-tail-hello",
			Exception: null, TraceId: null, SpanId: null, EventId: null,
			Properties: ImmutableDictionary<string, JsonElement>.Empty);
		broadcaster.Publish(ws, [candidate]);

		// Read frames until we find one whose data line contains the published message; SSE
		// framing = event:\s*<name>\ndata:\s*<payload>\n\n
		string? dataLine = null;
		var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var line = await reader.ReadLineAsync(cts.Token);
			if (line is null) break;
			if (line.StartsWith("data:", StringComparison.Ordinal) && line.Contains("live-tail-hello", StringComparison.Ordinal))
			{
				dataLine = line;
				break;
			}
		}

		dataLine.Should().NotBeNull("SSE frame carrying the published message should arrive within 10s");
		dataLine!.Should().Contain("live-tail-hello");
		// Partial rendered as _EventRow → row markup carries the events-row testid.
		dataLine.Should().Contain("events-row");
	}
}
