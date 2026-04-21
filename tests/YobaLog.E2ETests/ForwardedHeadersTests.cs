using System.Net;
using System.Net.Http.Json;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Caddy on the deploy host terminates TLS on :443 and reverse-proxies to 127.0.0.1:8082
// (doc/spec.md §11). For ASP.NET to see the correct scheme, UseForwardedHeaders must run
// with KnownProxies = [loopback] before anything that inspects IsHttps. Probe: the share
// endpoint echoes ctx.Request.Scheme in the returned URL — an observable, externally visible
// side effect, so a forged X-Forwarded-Proto from loopback should flip http → https in the URL.
[Collection(nameof(UiCollection))]
public sealed class ForwardedHeadersTests
{
	readonly WebAppFixture _app;

	public ForwardedHeadersTests(WebAppFixture app) => _app = app;

	[Fact]
	public async Task XForwardedProtoHttps_FromLoopback_FlipsRequestSchemeToHttps()
	{
		var ws = FreshWorkspace("fh-proto");
		await _app.SeedAsync(ws, Event(LogLevel.Information, "forwarded-probe"));

		using var http = await HttpAuthHelper.AuthenticatedClientAsync(_app);

		using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/ws/{ws}/share")
		{
			Content = JsonContent.Create(new
			{
				Kql = "events",
				TtlHours = 1,
				Columns = new[] { "Message" },
				Modes = new Dictionary<string, string>(),
			}),
		};
		req.Headers.Add("X-Forwarded-Proto", "https");

		using var resp = await http.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<ShareResponse>();
		body.Should().NotBeNull();
		body!.Url.Should().StartWith("https://",
			"ForwardedHeaders trusted loopback → rewrote ctx.Request.Scheme → share URL reflects https");
	}

	sealed record ShareResponse(string Url, DateTimeOffset ExpiresAt);
}
