using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaLog.Core.Admin;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
	readonly AdminAuthOptions _options;
	readonly IUserStore _userStore;

	public LoginModel(IOptions<AdminAuthOptions> options, IUserStore userStore)
	{
		_options = options.Value;
		_userStore = userStore;
	}

	[BindProperty] public string Username { get; set; } = "";
	[BindProperty] public string Password { get; set; } = "";
	[BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

	public string? Error { get; set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostAsync(CancellationToken ct)
	{
		if (!await AuthenticateAsync(Username, Password, ct).ConfigureAwait(false))
		{
			Error = "Invalid username or password";
			return Page();
		}

		var identity = new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, Username)],
			CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity));

		return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
			? LocalRedirect(ReturnUrl)
			: RedirectToPage("/Index");
	}

	// DB users take precedence when the store has any. Config-admin is a bootstrap/recovery
	// path — works when the DB is empty so there's always a way in. Once the first DB user is
	// created, the config credentials stop being honored (creator can delete themselves from
	// the DB to revert to config if needed).
	async Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
			return false;

		var users = await _userStore.ListAsync(ct).ConfigureAwait(false);
		return users.Count == 0
			? MatchesConfigAdmin(username, password)
			: await _userStore.VerifyAsync(username, password, ct).ConfigureAwait(false);
	}

	bool MatchesConfigAdmin(string username, string password)
	{
		if (!FixedTimeEquals(username, _options.Username))
			return false;

		if (!string.IsNullOrEmpty(_options.PasswordHash))
			return AdminPasswordHasher.Verify(password, _options.PasswordHash);

		return !string.IsNullOrEmpty(_options.Password)
			&& FixedTimeEquals(password, _options.Password);
	}

	static bool FixedTimeEquals(string a, string b)
	{
		var aBytes = Encoding.UTF8.GetBytes(a);
		var bBytes = Encoding.UTF8.GetBytes(b);
		return aBytes.Length == bBytes.Length
			&& CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
	}
}
