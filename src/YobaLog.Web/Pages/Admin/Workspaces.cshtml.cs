using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Admin;

namespace YobaLog.Web.Pages.Admin;

public sealed class WorkspacesModel : PageModel
{
	readonly IWorkspaceStore _store;

	public WorkspacesModel(IWorkspaceStore store)
	{
		_store = store;
	}

	public IReadOnlyList<WorkspaceInfo> Workspaces { get; private set; } = [];

	public string? ErrorMessage { get; private set; }

	[TempData]
	public string? FlashMessage { get; set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		var all = await _store.ListAsync(ct);
		Workspaces = [.. all.Where(w => !w.Id.IsSystem)];
	}

	public async Task<IActionResult> OnPostCreateAsync(
		[FromForm(Name = "id")] string? id,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id ?? "", out var ws))
		{
			ErrorMessage = "Invalid slug. Use lowercase letters, digits, dashes (2-40 chars, no leading dash).";
			await OnGetAsync(ct);
			return Page();
		}
		if (ws.IsSystem)
		{
			ErrorMessage = "The $system prefix is reserved.";
			await OnGetAsync(ct);
			return Page();
		}

		if (await _store.GetAsync(ws, ct) is not null)
		{
			ErrorMessage = $"Workspace '{ws.Value}' already exists.";
			await OnGetAsync(ct);
			return Page();
		}

		await _store.CreateAsync(ws, ct);
		FlashMessage = $"Created workspace '{ws.Value}'.";
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		[FromForm(Name = "id")] string? id,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id ?? "", out var ws) || ws.IsSystem)
			return RedirectToPage();

		var deleted = await _store.DeleteAsync(ws, ct);
		FlashMessage = deleted
			? $"Deleted workspace '{ws.Value}'."
			: $"Workspace '{ws.Value}' not found.";
		return RedirectToPage();
	}
}
