using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages.Admin;

public sealed class WorkspaceApiKeysModel : PageModel
{
	readonly IApiKeyAdmin _admin;

	public WorkspaceApiKeysModel(IApiKeyAdmin admin)
	{
		_admin = admin;
	}

	public WorkspaceId Workspace { get; private set; }

	public IReadOnlyList<ApiKeyInfo> Keys { get; private set; } = [];

	public string? ErrorMessage { get; private set; }

	[TempData] public string? FlashMessage { get; set; }

	// Plaintext is surfaced exactly once via TempData right after creation, then gone for good.
	[TempData] public string? RevealedToken { get; set; }
	[TempData] public string? RevealedTitle { get; set; }

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws) || ws.IsSystem)
			return NotFound();

		Workspace = ws;
		Keys = await _admin.ListAsync(ws, ct);
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(
		string id,
		[FromForm(Name = "title")] string? title,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws) || ws.IsSystem)
			return NotFound();

		var created = await _admin.CreateAsync(ws, title, ct);
		RevealedToken = created.Plaintext;
		RevealedTitle = created.Info.Title;
		FlashMessage = $"Created API key {created.Info.Prefix}…";
		return RedirectToPage(new { id = ws.Value });
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		string id,
		[FromForm(Name = "keyId")] string? keyId,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws) || ws.IsSystem || string.IsNullOrWhiteSpace(keyId))
			return NotFound();

		var deleted = await _admin.DeleteAsync(ws, keyId, ct);
		FlashMessage = deleted ? "API key deleted." : "API key not found.";
		return RedirectToPage(new { id = ws.Value });
	}
}
