using System.Net;
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
		using var client = _factory.CreateClient();
		using var resp = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "wrong",
			}));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("Invalid username or password");
	}

	[Fact]
	public async Task CorrectCredentials_SetsCookie_AndRedirects()
	{
		using var client = _factory.CreateClient(NoRedirect());
		using var resp = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "s3cret",
			}));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
		cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookies", StringComparison.Ordinal));
	}

	[Fact]
	public async Task HashedPassword_AllowsLogin()
	{
		var hash = AdminPasswordHasher.Hash("hashed-secret");
		using var hashFactory = new WebApplicationFactory<Program>()
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

		using var client = hashFactory.CreateClient(NoRedirect());
		using var resp = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "hashed-secret",
			}));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
		cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookies", StringComparison.Ordinal));

		await hashFactory.DisposeAsync();
	}

	[Fact]
	public async Task HashedPassword_WrongInput_Rejects()
	{
		var hash = AdminPasswordHasher.Hash("hashed-secret");
		using var hashFactory = new WebApplicationFactory<Program>()
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

		using var client = hashFactory.CreateClient();
		using var resp = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "different",
			}));

		(await resp.Content.ReadAsStringAsync()).Should().Contain("Invalid username or password");
		await hashFactory.DisposeAsync();
	}

	[Fact]
	public async Task IngestionEndpoint_StaysAnonymous()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw")
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
