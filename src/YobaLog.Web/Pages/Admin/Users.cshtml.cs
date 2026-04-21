using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core.Admin;

namespace YobaLog.Web.Pages.Admin;

public sealed class UsersModel : PageModel
{
	readonly IUserStore _store;

	public UsersModel(IUserStore store)
	{
		_store = store;
	}

	public IReadOnlyList<UserInfo> Users { get; private set; } = [];

	public string? ErrorMessage { get; private set; }

	[TempData]
	public string? FlashMessage { get; set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		Users = await _store.ListAsync(ct);
	}

	public async Task<IActionResult> OnPostCreateAsync(
		[FromForm(Name = "username")] string? username,
		[FromForm(Name = "password")] string? password,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
		{
			ErrorMessage = "Username and password are required.";
			await OnGetAsync(ct);
			return Page();
		}

		var trimmed = username.Trim();
		try
		{
			await _store.CreateAsync(trimmed, password, ct);
		}
		catch (InvalidOperationException ex)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		FlashMessage = $"Created user '{trimmed}'.";
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostChangePasswordAsync(
		[FromForm(Name = "username")] string? username,
		[FromForm(Name = "newPassword")] string? newPassword,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(newPassword))
		{
			ErrorMessage = "New password is required.";
			await OnGetAsync(ct);
			return Page();
		}

		try
		{
			await _store.UpdatePasswordAsync(username, newPassword, ct);
		}
		catch (InvalidOperationException ex)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		FlashMessage = $"Updated password for '{username}'.";
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		[FromForm(Name = "username")] string? username,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(username))
			return RedirectToPage();

		var deleted = await _store.DeleteAsync(username, ct);
		FlashMessage = deleted
			? $"Deleted user '{username}'."
			: $"User '{username}' not found.";
		return RedirectToPage();
	}
}
