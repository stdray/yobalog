namespace YobaLog.E2ETests.Pages;

public sealed class LoginPage(IPage page)
{
	public async Task GotoAsync(string? returnUrl = null)
	{
		var path = returnUrl is null ? "/Login" : $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
		await page.GotoAsync(path);
	}

	public async Task SubmitAsync(string username, string password)
	{
		await page.GetByTestId("login-username").FillAsync(username);
		await page.GetByTestId("login-password").FillAsync(password);
		await page.GetByTestId("login-submit").ClickAsync();
		// ClickAsync on a submit button auto-waits for navigation in Playwright. Don't use
		// WaitForLoadStateAsync(NetworkIdle) — htmx keep-alive connections never settle and the
		// wait times out at 30s. Rely on the caller's Expect(...) auto-retry to absorb any residual
		// latency of the redirect.
	}

	public Task AssertErrorVisibleAsync() =>
		Expect(page.GetByTestId("login-error")).ToBeVisibleAsync();

	public Task AssertStillOnLoginAsync() =>
		Expect(page.GetByTestId("login-submit")).ToBeVisibleAsync();
}
