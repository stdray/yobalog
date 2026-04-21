using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class AdminWorkspacesTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	public AdminWorkspacesTests(WebAppFixture app) => _app = app;

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
	public async Task Create_then_navigate_then_delete_full_lifecycle()
	{
		var slug = $"admin-e2e-{Guid.NewGuid():N}"[..20];

		await _page!.GotoAsync("/admin/workspaces");
		await _page.GetByTestId("admin-workspace-slug").FillAsync(slug);
		await _page.GetByTestId("admin-workspace-create").ClickAsync();

		// New row appears in the admin listing.
		var row = _page.Locator($"[data-testid=admin-workspace-row][data-workspace-id='{slug}']");
		await Expect(row).ToBeVisibleAsync();

		// Regression: navigating to a freshly admin-created workspace must not 500 with
		// "no such table: SavedQueries" — bug a3acf78 covered by SqliteWorkspaceStoreTests,
		// reproduced here end-to-end through the UI.
		await _page.GotoAsync($"/ws/{slug}");
		await Expect(_page.GetByTestId("events-empty")).ToBeVisibleAsync();

		// Delete: auto-accept the confirm() dialog the Delete button fires.
		await _page.GotoAsync("/admin/workspaces");
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await row.GetByTestId("admin-workspace-delete").ClickAsync();

		// Row gone + db files removed.
		await Expect(row).Not.ToBeAttachedAsync();
		var dataDir = _app.Services.GetRequiredService<IOptions<SqliteLogStoreOptions>>().Value.DataDirectory;
		File.Exists(Path.Combine(dataDir, $"{slug}.logs.db")).Should().BeFalse();
		File.Exists(Path.Combine(dataDir, $"{slug}.meta.db")).Should().BeFalse();
	}
}
