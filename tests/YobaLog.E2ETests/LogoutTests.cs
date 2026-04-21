namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class LogoutTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public LogoutTests(WebAppFixture app, ITestOutputHelper output)
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
	public async Task Logout_clears_cookie_and_next_request_redirects_to_login()
	{
		// Authenticated context lands on / with navbar Sign-out button.
		await _page!.GotoAsync("/");
		await Expect(_page.GetByTestId("workspace-list")).ToBeVisibleAsync();

		await _page.GetByTestId("logout-submit").ClickAsync();

		// After /Logout, server redirects to /Login; loading / again re-challenges →
		// lands on /Login again.
		await _page.GotoAsync("/");
		await Expect(_page.GetByTestId("login-submit")).ToBeVisibleAsync();
		_page.Url.Should().Contain("/Login");
	}
}
