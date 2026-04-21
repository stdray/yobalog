using System.Text.RegularExpressions;

namespace YobaLog.E2ETests.Infrastructure;

// Cookie-based login helper for HttpClient-level tests (SSE streaming, /api/* endpoints).
// Since antiforgery is now enforced on /Login, we GET the form first, parse the token, then
// POST with both token and antiforgery cookie preserved via HttpClientHandler.UseCookies.
public static class HttpAuthHelper
{
	public static async Task<HttpClient> AuthenticatedClientAsync(WebAppFixture app)
	{
		var handler = new HttpClientHandler { UseCookies = true };
		var http = new HttpClient(handler) { BaseAddress = new Uri(app.BaseUrl) };

		using var loginPage = await http.GetAsync("/Login");
		var html = await loginPage.Content.ReadAsStringAsync();
		var token = ExtractAntiforgeryToken(html);

		using var loginResp = await http.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["Username"] = WebAppFixture.AdminUsername,
			["Password"] = WebAppFixture.AdminPassword,
			["__RequestVerificationToken"] = token,
		}));
		loginResp.IsSuccessStatusCode.Should().BeTrue("login should succeed with a valid token");
		return http;
	}

	static string ExtractAntiforgeryToken(string html)
	{
		var match = Regex.Match(html, @"name=""__RequestVerificationToken""\s+[^>]*value=""([^""]+)""");
		if (!match.Success)
			throw new InvalidOperationException("could not find __RequestVerificationToken on /Login");
		return match.Groups[1].Value;
	}
}
