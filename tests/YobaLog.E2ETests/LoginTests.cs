namespace YobaLog.E2ETests;

public sealed class LoginTests : IClassFixture<WebAppFixture>, IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	public LoginTests(WebAppFixture app) => _app = app;

	public async Task InitializeAsync()
	{
		// Unauthenticated context — login flow is what's under test here.
		_ctx = await _app.NewContextAsync(authenticated: false);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) await _ctx.CloseAsync();
	}

	[Fact]
	public async Task WrongCreds_ShowAlert()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();

		await login.SubmitAsync(WebAppFixture.AdminUsername, "definitely-wrong");

		await login.AssertErrorVisibleAsync();
		await login.AssertStillOnLoginAsync();
	}

	[Fact]
	public async Task CorrectCreds_RedirectToIndex()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();

		await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

		// Post-login lands on /; workspace-list should render with at least one workspace.
		// (Index always shows $system + configured workspaces.)
		await Expect(_page!.GetByTestId("workspace-list")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("workspace-link").First).ToBeVisibleAsync();
	}
}
