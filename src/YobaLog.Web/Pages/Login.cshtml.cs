using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class LoginModel : PageModel
{
	readonly AdminAuthOptions _options;

	public LoginModel(IOptions<AdminAuthOptions> options)
	{
		_options = options.Value;
	}

	[BindProperty] public string Username { get; set; } = "";
	[BindProperty] public string Password { get; set; } = "";
	[BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

	public string? Error { get; set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostAsync()
	{
		if (!MatchesAdmin(Username, Password))
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

	bool MatchesAdmin(string username, string password) =>
		FixedTimeEquals(username, _options.Username) &&
		FixedTimeEquals(password, _options.Password);

	static bool FixedTimeEquals(string a, string b)
	{
		var aBytes = Encoding.UTF8.GetBytes(a);
		var bBytes = Encoding.UTF8.GetBytes(b);
		return aBytes.Length == bBytes.Length
			&& CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
	}
}
