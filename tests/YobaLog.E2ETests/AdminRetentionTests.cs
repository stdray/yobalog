using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Admin;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;

namespace YobaLog.E2ETests;

// /admin/retention end-to-end: pick a workspace, bind a saved query to a retain-days figure,
// confirm the row appears. Then prove the UX invariant from spec §3 — a saved query referenced
// by a retention policy can't be deleted from the workspace page; the delete button flashes
// an error instead. Dispose cleans up the created policy so the shared UiCollection fixture
// doesn't leak state.
[Collection(nameof(UiCollection))]
public sealed class AdminRetentionTests : IAsyncLifetime
{
	readonly WebAppFixture _app;
	IBrowserContext? _ctx;
	IPage? _page;

	// Ws + saved query live across the whole test; we seed them directly (not through UI) so
	// the test focuses on the retention admin flow itself.
	WorkspaceId _ws;
	const string SavedName = "retention-target";

	public AdminRetentionTests(WebAppFixture app) => _app = app;

	public async Task InitializeAsync()
	{
		_ctx = await _app.NewContextAsync();
		_page = await _ctx.NewPageAsync();

		_ws = WorkspaceId.Parse($"ret-{Guid.NewGuid():N}"[..10]);
		// Create via IWorkspaceStore so it shows up in /admin/retention's workspace dropdown.
		// SqliteWorkspaceStore.CreateAsync initializes all four meta stores (saved queries / masking
		// / share links / api keys) + logs.db, same path the admin UI would take.
		await _app.Services.GetRequiredService<IWorkspaceStore>().CreateAsync(_ws, CancellationToken.None);
		await _app.Services.GetRequiredService<ISavedQueryStore>()
			.UpsertAsync(_ws, SavedName, "events | where Level >= 4", CancellationToken.None);
	}

	public async Task DisposeAsync()
	{
		var store = _app.Services.GetRequiredService<IRetentionPolicyStore>();
		foreach (var p in await store.ListByWorkspaceAsync(_ws, CancellationToken.None))
			await store.DeleteAsync(_ws, p.SavedQuery, CancellationToken.None);
		// Drop the workspace so /admin/workspaces listing + dropdown stay clean for the next test.
		await _app.Services.GetRequiredService<IWorkspaceStore>().DeleteAsync(_ws, CancellationToken.None);
		if (_ctx is not null) await _ctx.CloseAsync();
	}

	[Fact]
	public async Task Create_policy_then_saved_query_delete_is_blocked()
	{
		// --- Create policy via /admin/retention ----------------------------------------------
		await _page!.GotoAsync("/admin/retention");
		await _page.GetByTestId("retention-workspace").SelectOptionAsync(_ws.Value);
		await _page.GetByTestId("retention-saved").FillAsync(SavedName);
		await _page.GetByTestId("retention-days").FillAsync("42");
		await _page.GetByTestId("retention-create").ClickAsync();

		var row = _page.Locator($"[data-testid=retention-row][data-workspace='{_ws.Value}'][data-saved='{SavedName}']");
		await Expect(row).ToBeVisibleAsync();

		// --- Try to delete the referenced saved query from /ws/{id} → flash error blocks it --
		await _page.GotoAsync($"/ws/{_ws.Value}");
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		// The chip's sibling form has an `×` delete button (no data-testid — still DaisyUI ghost).
		// Target it by the join group that contains our saved-name anchor.
		var chip = _page.Locator($"[data-testid=saved-query-chip][data-saved-name='{SavedName}']");
		await Expect(chip).ToBeVisibleAsync();
		await chip.Locator("xpath=..").Locator("button[type=submit]").ClickAsync();

		await Expect(_page.GetByTestId("workspace-flash-error")).ToBeVisibleAsync();
		await Expect(chip).ToBeVisibleAsync(); // still there — the delete was blocked.

		// --- Delete the policy → saved query delete now succeeds -----------------------------
		await _page.GotoAsync("/admin/retention");
		await row.GetByTestId("retention-delete").ClickAsync();
		await Expect(row).Not.ToBeAttachedAsync();

		await _page.GotoAsync($"/ws/{_ws.Value}");
		await chip.Locator("xpath=..").Locator("button[type=submit]").ClickAsync();
		await Expect(chip).Not.ToBeAttachedAsync();
	}
}
