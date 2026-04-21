using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Sharing;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// Share-link contract tests — the share modal's UI is a thin wrapper over POST /api/ws/{id}/share
// + GET /share/{ws}/{id}.tsv. Testing those directly (HttpClient) covers the production surface
// without re-implementing the modal in Playwright; modal ergonomics are tested manually via MCP.
[Collection(nameof(UiCollection))]
public sealed class ShareLinkTests
{
	readonly WebAppFixture _app;

	public ShareLinkTests(WebAppFixture app) => _app = app;

	[Fact]
	public async Task Generate_link_then_fetch_TSV_returns_event_rows()
	{
		var ws = FreshWorkspace("share-ok");
		await _app.SeedAsync(ws,
			Event(LogLevel.Error, "share-tsv-visible"),
			Event(LogLevel.Information, "share-tsv-other"));

		using var http = await AuthenticatedClientAsync();
		using var createResp = await http.PostAsJsonAsync($"/api/ws/{ws}/share", new
		{
			Kql = "events",
			TtlHours = 1,
			Columns = new[] { "Message", "Level" },
			Modes = new Dictionary<string, string>(),
		});
		createResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await createResp.Content.ReadFromJsonAsync<ShareResponse>();
		body.Should().NotBeNull();

		// Anonymous GET — no cookie needed.
		using var anon = new HttpClient();
		using var tsvResp = await anon.GetAsync(body!.Url);
		tsvResp.StatusCode.Should().Be(HttpStatusCode.OK);
		tsvResp.Content.Headers.ContentType!.MediaType.Should().Be("text/tab-separated-values");
		var tsv = await tsvResp.Content.ReadAsStringAsync();
		tsv.Should().Contain("share-tsv-visible");
		tsv.Should().Contain("share-tsv-other");
	}

	[Fact]
	public async Task Expired_link_returns_410_and_gets_swept()
	{
		var wsSlug = FreshWorkspace("share-exp");
		await _app.SeedAsync(wsSlug, Event(LogLevel.Error, "share-expired-evt"));
		var ws = WorkspaceId.Parse(wsSlug);

		// Create a link directly via the store so we can give it a past expiry.
		var store = _app.Services.GetRequiredService<IShareLinkStore>();
		var link = await store.CreateAsync(
			ws,
			"events",
			DateTimeOffset.UtcNow.AddMinutes(-5),
			ImmutableArray.Create("Message"),
			ImmutableDictionary<string, MaskMode>.Empty,
			CancellationToken.None);

		using var anon = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
		using var resp = await anon.GetAsync($"/share/{wsSlug}/{link.Id}.tsv");
		resp.StatusCode.Should().Be(HttpStatusCode.Gone);

		// Lazy-delete side effect: a second GET should 404.
		using var resp2 = await anon.GetAsync($"/share/{wsSlug}/{link.Id}.tsv");
		resp2.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	async Task<HttpClient> AuthenticatedClientAsync()
	{
		var handler = new HttpClientHandler { UseCookies = true };
		var http = new HttpClient(handler) { BaseAddress = new Uri(_app.BaseUrl) };
		using var resp = await http.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["Username"] = WebAppFixture.AdminUsername,
			["Password"] = WebAppFixture.AdminPassword,
		}));
		resp.IsSuccessStatusCode.Should().BeTrue();
		return http;
	}

	sealed record ShareResponse(string Url, DateTimeOffset ExpiresAt);
}
