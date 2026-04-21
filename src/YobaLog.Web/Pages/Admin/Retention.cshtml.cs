using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;

namespace YobaLog.Web.Pages.Admin;

public sealed class RetentionModel : PageModel
{
	readonly IWorkspaceStore _workspaces;
	readonly ISavedQueryStore _savedQueries;
	readonly IRetentionPolicyStore _policies;

	public RetentionModel(
		IWorkspaceStore workspaces,
		ISavedQueryStore savedQueries,
		IRetentionPolicyStore policies)
	{
		_workspaces = workspaces;
		_savedQueries = savedQueries;
		_policies = policies;
	}

	public IReadOnlyList<RetentionPolicy> Policies { get; private set; } = [];

	public IReadOnlyList<WorkspaceInfo> Workspaces { get; private set; } = [];

	public string? ErrorMessage { get; private set; }

	[TempData]
	public string? FlashMessage { get; set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		Policies = await _policies.ListAsync(ct);
		var all = await _workspaces.ListAsync(ct);
		// Hide $system — retention for it is governed by RetentionOptions.SystemRetainDays, not
		// by saved-query-based policies, and mixing it into the dropdown invites confusion.
		Workspaces = [.. all.Where(w => !w.Id.IsSystem)];
	}

	public async Task<IActionResult> OnPostCreateAsync(
		[FromForm(Name = "workspace")] string? workspace,
		[FromForm(Name = "savedQuery")] string? savedQuery,
		[FromForm(Name = "retainDays")] int retainDays,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(savedQuery))
		{
			ErrorMessage = "Workspace and saved query are required.";
			await OnGetAsync(ct);
			return Page();
		}
		if (!WorkspaceId.TryParse(workspace, out var ws) || ws.IsSystem)
		{
			ErrorMessage = "Invalid workspace.";
			await OnGetAsync(ct);
			return Page();
		}
		if (retainDays <= 0)
		{
			ErrorMessage = "Retain days must be a positive integer.";
			await OnGetAsync(ct);
			return Page();
		}

		// Saved query must already exist — retention refers to it by name, editor lives in the
		// workspace's own /ws/{id} KQL form.
		var saved = await _savedQueries.GetByNameAsync(ws, savedQuery, ct);
		if (saved is null)
		{
			ErrorMessage = $"Saved query '{savedQuery}' not found in workspace '{ws.Value}'. Create it first at /ws/{ws.Value}.";
			await OnGetAsync(ct);
			return Page();
		}

		await _policies.UpsertAsync(
			new RetentionPolicy { Workspace = ws.Value, SavedQuery = savedQuery, RetainDays = retainDays },
			ct);
		FlashMessage = $"Policy saved: {ws.Value} / {savedQuery} → {retainDays}d.";
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		[FromForm(Name = "workspace")] string? workspace,
		[FromForm(Name = "savedQuery")] string? savedQuery,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(savedQuery)
			|| !WorkspaceId.TryParse(workspace, out var ws))
			return RedirectToPage();

		var deleted = await _policies.DeleteAsync(ws, savedQuery, ct);
		FlashMessage = deleted
			? $"Deleted policy: {ws.Value} / {savedQuery}."
			: $"Policy not found: {ws.Value} / {savedQuery}.";
		return RedirectToPage();
	}
}
