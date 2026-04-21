using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using YobaLog.Core.Auth;

namespace YobaLog.Tests.Web;

public sealed class LoginTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;

	public LoginTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-login-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["SqliteLogStore:DataDirectory"] = _tempDir,
						["ApiKeys:Keys:0:Token"] = "key",
						["ApiKeys:Keys:0:Workspace"] = "dev",
						["Admin:Username"] = "admin",
						["Admin:Password"] = "s3cret",
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		_ = _factory.CreateClient();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	static WebApplicationFactoryClientOptions NoRedirect() => new()
	{
		AllowAutoRedirect = false,
	};

	// Antiforgery is enforced on the Login POST now that the admin section has mutating forms.
	// Tests that POST /Login need to GET it first to capture the token + cookie.
	static async Task<(HttpClient Client, FormUrlEncodedContent Form)> PreparePostAsync(
		WebApplicationFactory<Program> factory,
		IReadOnlyDictionary<string, string> fields)
	{
		// WebApplicationFactory's TestServer uses an in-memory handler that already manages cookies
		// via CookieContainer on the default client, and CreateClient's options let us disable
		// redirects so the post-login 302 is observable.
		var client = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = true,
		});
		using var getResp = await client.GetAsync("/Login");
		var html = await getResp.Content.ReadAsStringAsync();
		var tokenMatch = Regex.Match(html, @"name=""__RequestVerificationToken""\s+[^>]*value=""([^""]+)""");
		if (!tokenMatch.Success)
			throw new InvalidOperationException("could not find __RequestVerificationToken on /Login");

		var body = new Dictionary<string, string>(fields)
		{
			["__RequestVerificationToken"] = tokenMatch.Groups[1].Value,
		};
		return (client, new FormUrlEncodedContent(body));
	}

	[Fact]
	public async Task UnauthIndex_Redirects_ToLogin()
	{
		using var client = _factory.CreateClient(NoRedirect());
		using var resp = await client.GetAsync("/");

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/Login");
	}

	[Fact]
	public async Task WrongPassword_StaysOnLogin()
	{
		var (client, form) = await PreparePostAsync(_factory, new Dictionary<string, string>
		{
			["Username"] = "admin",
			["Password"] = "wrong",
		});
		using (client)
		using (form)
		{
			using var resp = await client.PostAsync("/Login", form);
			resp.StatusCode.Should().Be(HttpStatusCode.OK);
			var body = await resp.Content.ReadAsStringAsync();
			body.Should().Contain("Invalid username or password");
		}
	}

	[Fact]
	public async Task CorrectCredentials_SetsCookie_AndRedirects()
	{
		var (client, form) = await PreparePostAsync(_factory, new Dictionary<string, string>
		{
			["Username"] = "admin",
			["Password"] = "s3cret",
		});
		using (client)
		using (form)
		{
			using var resp = await client.PostAsync("/Login", form);
			resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
			resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
			cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookies", StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task HashedPassword_AllowsLogin()
	{
		var hash = AdminPasswordHasher.Hash("hashed-secret");
		await using var hashFactory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["SqliteLogStore:DataDirectory"] = _tempDir,
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = hash,
					});
				});
			});

		var (client, form) = await PreparePostAsync(hashFactory, new Dictionary<string, string>
		{
			["Username"] = "admin",
			["Password"] = "hashed-secret",
		});
		using (client)
		using (form)
		{
			using var resp = await client.PostAsync("/Login", form);
			resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
			resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
			cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookies", StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task HashedPassword_WrongInput_Rejects()
	{
		var hash = AdminPasswordHasher.Hash("hashed-secret");
		await using var hashFactory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["SqliteLogStore:DataDirectory"] = _tempDir,
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = hash,
					});
				});
			});

		var (client, form) = await PreparePostAsync(hashFactory, new Dictionary<string, string>
		{
			["Username"] = "admin",
			["Password"] = "different",
		});
		using (client)
		using (form)
		{
			using var resp = await client.PostAsync("/Login", form);
			(await resp.Content.ReadAsStringAsync()).Should().Contain("Invalid username or password");
		}
	}

	[Fact]
	public async Task MissingAntiforgeryToken_IsRejected()
	{
		using var client = _factory.CreateClient(NoRedirect());
		using var resp = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "s3cret",
			}));

		// ASP.NET Core returns 400 Bad Request when the antiforgery validation fails.
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task IngestionEndpoint_StaysAnonymous()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/clef")
		{
			Content = new StringContent(
				"""{"@t":"2026-04-19T10:00:00Z","@m":"x"}""",
				System.Text.Encoding.UTF8,
				"application/vnd.serilog.clef"),
		};
		req.Headers.Add("X-Seq-ApiKey", "key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);
	}
}
