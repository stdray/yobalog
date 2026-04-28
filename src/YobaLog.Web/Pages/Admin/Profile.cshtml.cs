using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages.Admin;

// Per-user admin-token CRUD. Lists / creates / revokes the personal access tokens belonging
// to the currently-logged-in user. Other users' tokens are invisible — listing is filtered
// server-side by `User.Identity?.Name`. Plaintext is shown exactly once on create (same flow
// as /ws/{id}/admin/api-keys). Soft-delete acts as "revoke" — row stays for audit.
public sealed class ProfileModel : PageModel
{
    readonly IAdminTokenAdmin _admin;

    public ProfileModel(IAdminTokenAdmin admin)
    {
        _admin = admin;
    }

    public string Username { get; private set; } = string.Empty;
    public IReadOnlyList<AdminTokenInfo> Tokens { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    // Plaintext is surfaced exactly once via TempData right after creation, then gone for good.
    [TempData] public string? RevealedToken { get; set; }
    [TempData] public string? RevealedDescription { get; set; }
    [TempData] public string? FlashMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm(Name = "description")] string? description,
        CancellationToken ct)
    {
        await LoadAsync(ct);
        if (string.IsNullOrEmpty(Username))
        {
            ErrorMessage = "Cannot create token: no authenticated user.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            ErrorMessage = "Description is required.";
            return Page();
        }

        var created = await _admin.CreateAsync(Username, description.Trim(), ct);
        RevealedToken = created.Plaintext;
        RevealedDescription = created.Info.Description;
        FlashMessage = $"Created admin token {created.Info.TokenPrefix}...";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(
        [FromForm(Name = "id")] long? id,
        CancellationToken ct)
    {
        await LoadAsync(ct);
        if (id is null)
        {
            ErrorMessage = "Missing token id.";
            return Page();
        }

        // Per-user isolation: only revoke a token if it belongs to the logged-in user.
        // Without this, /admin/profile would let any admin revoke any other admin's token by
        // guessing the Id. The store's SoftDelete is by-id, so the page does the ownership
        // check before delegating.
        if (!Tokens.Any(t => t.Id == id.Value))
        {
            ErrorMessage = $"Token {id.Value} not found among your tokens.";
            return Page();
        }

        var deleted = await _admin.SoftDeleteAsync(id.Value, ct);
        FlashMessage = deleted ? "Token revoked." : "Token not found.";
        return RedirectToPage();
    }

    async Task LoadAsync(CancellationToken ct)
    {
        Username = User.Identity?.Name ?? string.Empty;
        Tokens = string.IsNullOrEmpty(Username)
            ? []
            : await _admin.ListByUsernameAsync(Username, ct);
    }
}
