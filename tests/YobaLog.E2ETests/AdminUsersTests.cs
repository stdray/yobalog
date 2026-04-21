using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Admin;

namespace YobaLog.E2ETests;

// Multi-admin lifecycle: create a DB user via the admin UI, log in as that user, rotate
// password, verify new accepted / old rejected, delete the user, confirm login no longer
// works. Once ≥1 DB user exists, the appsettings Admin fallback is bypassed — covered via
// a negative assertion after create.
[Collection(nameof(UiCollection))]
public sealed class AdminUsersTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	readonly ITestOutputHelper _output;
	IBrowserContext? _ctx;
	IPage? _page;

	public AdminUsersTests(WebAppFixture app, ITestOutputHelper output)
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
		// Clean up any DB users the test created so the shared UiCollection fixture stays usable
		// for the next class (LoginTests expects the appsettings fallback to work).
		var store = _app.Services.GetRequiredService<IUserStore>();
		var users = await store.ListAsync(CancellationToken.None);
		foreach (var u in users)
			await store.DeleteAsync(u.Username, CancellationToken.None);

		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, _output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task User_lifecycle_create_rotate_delete_with_auth_checks()
	{
		// Unique username keeps the test isolated even if the dispose cleanup runs late.
		var username = $"e2e-{Guid.NewGuid():N}"[..12];
		const string originalPassword = "initial-pw-xyz";
		const string rotatedPassword = "rotated-pw-abc";

		// --- Create via UI -------------------------------------------------------------------
		await _page!.GotoAsync("/admin/users");
		await _page.GetByTestId("admin-user-name").FillAsync(username);
		await _page.GetByTestId("admin-user-password").FillAsync(originalPassword);
		await _page.GetByTestId("admin-user-create").ClickAsync();

		var row = _page.Locator($"[data-testid=admin-user-row][data-user-name='{username}']");
		await Expect(row).ToBeVisibleAsync();

		// --- Config-admin is now locked out (DB has a user → config fallback bypassed) ------
		await using (var configCtx = await _app.NewContextAsync(authenticated: false))
		{
			var configPage = await configCtx.NewPageAsync();
			await configPage.GotoAsync("/Login");
			await configPage.GetByTestId("login-username").FillAsync(WebAppFixture.AdminUsername);
			await configPage.GetByTestId("login-password").FillAsync(WebAppFixture.AdminPassword);
			await configPage.GetByTestId("login-submit").ClickAsync();
			await Expect(configPage.GetByTestId("login-error")).ToBeVisibleAsync();
		}

		// --- Login as the new DB user works --------------------------------------------------
		await using (var userCtx = await _app.NewContextAsync(authenticated: false))
		{
			var userPage = await userCtx.NewPageAsync();
			await userPage.GotoAsync("/Login");
			await userPage.GetByTestId("login-username").FillAsync(username);
			await userPage.GetByTestId("login-password").FillAsync(originalPassword);
			await userPage.GetByTestId("login-submit").ClickAsync();
			await Expect(userPage.GetByTestId("workspace-list")).ToBeVisibleAsync();
		}

		// --- Rotate password via UI; verify old rejected / new accepted ----------------------
		await _page.GotoAsync("/admin/users");
		var rotateForm = _page.Locator($"[data-testid=admin-user-row][data-user-name='{username}']");
		await rotateForm.GetByTestId("admin-user-newpass").FillAsync(rotatedPassword);
		await rotateForm.GetByTestId("admin-user-rotate").ClickAsync();
		// Flash confirms update.
		await Expect(_page.Locator(".alert-success")).ToBeVisibleAsync();

		await using (var rotatedCtx = await _app.NewContextAsync(authenticated: false))
		{
			var p = await rotatedCtx.NewPageAsync();
			await p.GotoAsync("/Login");
			await p.GetByTestId("login-username").FillAsync(username);
			await p.GetByTestId("login-password").FillAsync(originalPassword);
			await p.GetByTestId("login-submit").ClickAsync();
			await Expect(p.GetByTestId("login-error")).ToBeVisibleAsync();

			await p.GetByTestId("login-username").FillAsync(username);
			await p.GetByTestId("login-password").FillAsync(rotatedPassword);
			await p.GetByTestId("login-submit").ClickAsync();
			await Expect(p.GetByTestId("workspace-list")).ToBeVisibleAsync();
		}

		// --- Delete user + confirm login with the rotated password is now rejected -----------
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await rotateForm.GetByTestId("admin-user-delete").ClickAsync();
		await Expect(rotateForm).Not.ToBeAttachedAsync();

		await using (var gone = await _app.NewContextAsync(authenticated: false))
		{
			var p = await gone.NewPageAsync();
			await p.GotoAsync("/Login");
			await p.GetByTestId("login-username").FillAsync(username);
			await p.GetByTestId("login-password").FillAsync(rotatedPassword);
			await p.GetByTestId("login-submit").ClickAsync();
			await Expect(p.GetByTestId("login-error")).ToBeVisibleAsync();
		}
	}
}
