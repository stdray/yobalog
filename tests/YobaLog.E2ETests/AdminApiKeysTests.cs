using System.Net;
using System.Text;

namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class AdminApiKeysTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public AdminApiKeysTests(WebAppFixture app, ITestOutputHelper output)
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
	public async Task Create_reveals_plaintext_ingests_then_delete_rejects()
	{
		// Fresh workspace so the api-keys table is dedicated to this test (UiCollection is shared).
		await _page!.GotoAsync("/admin/workspaces");
		var slug = $"keys-e2e-{Guid.NewGuid():N}"[..20];
		await _page.GetByTestId("admin-workspace-slug").FillAsync(slug);
		await _page.GetByTestId("admin-workspace-create").ClickAsync();

		// Create a key with a title, capture the plaintext revealed once.
		await _page.GotoAsync($"/ws/{slug}/admin/api-keys");
		await _page.GetByTestId("api-key-title").FillAsync("e2e-ingest");
		await _page.GetByTestId("api-key-create").ClickAsync();

		var revealed = _page.GetByTestId("api-key-revealed");
		await Expect(revealed).ToBeVisibleAsync();
		var plaintext = await revealed.InputValueAsync();
		plaintext.Should().HaveLength(22);

		// Key row is visible in the listing with the 6-char prefix (shown, plaintext isn't).
		var prefix = plaintext[..6];
		var keyRow = _page.Locator($"[data-testid=api-key-row][data-key-prefix='{prefix}']");
		await Expect(keyRow).ToBeVisibleAsync();

		// Ingestion with the freshly-created token lands at /compat/seq; server responds 201.
		using var anon = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
		var cleF = """{"@t":"2026-04-21T10:00:00Z","@m":"admin-api-key-ingest"}""";
		using var ingestReq = new HttpRequestMessage(HttpMethod.Post, "/compat/seq/api/events/raw")
		{
			Content = new StringContent(cleF, Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		ingestReq.Headers.Add("X-Seq-ApiKey", plaintext);
		using var ingestResp = await anon.SendAsync(ingestReq);
		ingestResp.StatusCode.Should().Be(HttpStatusCode.Created);

		// Delete key → second ingest with same plaintext must be rejected.
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await keyRow.GetByTestId("api-key-delete").ClickAsync();
		await Expect(keyRow).Not.ToBeAttachedAsync();

		using var afterReq = new HttpRequestMessage(HttpMethod.Post, "/compat/seq/api/events/raw")
		{
			Content = new StringContent(cleF, Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		afterReq.Headers.Add("X-Seq-ApiKey", plaintext);
		using var afterResp = await anon.SendAsync(afterReq);
		afterResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
