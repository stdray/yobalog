using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Web;

public sealed class IngestionEndpointTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;

	public IngestionEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-web-" + Guid.NewGuid().ToString("N")[..8]);
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
						["ApiKeys:Keys:0:Token"] = "test-key",
						["ApiKeys:Keys:0:Workspace"] = "integration",
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		// Touch the factory so hosted services run (WorkspaceBootstrapper creates workspaces).
		_ = _factory.Services;
		_ = _factory.CreateClient();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	[Fact]
	public async Task Post_WithValidKey_AcceptsEvents()
	{
		using var client = _factory.CreateClient();
		var body =
			"""{"@t":"2026-04-19T10:00:00Z","@l":"Information","@m":"hello"}""" + "\n" +
			"""{"@t":"2026-04-19T10:00:01Z","@l":"Error","@m":"boom"}""";

		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/clef")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		req.Headers.Add("X-Seq-ApiKey", "test-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);

		// Wait for pipeline to flush (StopAsync drains on dispose, but for assertion we need to poll).
		await WaitForEventsAsync(expected: 2);

		var store = (ILogStore)_factory.Services.GetService(typeof(ILogStore))!;
		var count = await store.CountAsync(
			WorkspaceId.Parse("integration"),
			new LogQuery(PageSize: 10),
			CancellationToken.None);
		count.Should().Be(2);
	}

	[Fact]
	public async Task Post_MissingKey_Unauthorized()
	{
		using var client = _factory.CreateClient();
		using var resp = await client.PostAsync(
			"/api/v1/ingest/clef",
			new StringContent("""{"@t":"2026-04-19T10:00:00Z"}""", Encoding.UTF8, "application/vnd.serilog.clef"));

		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_WrongKey_Unauthorized()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/clef")
		{
			Content = new StringContent("""{"@t":"2026-04-19T10:00:00Z"}""", Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		req.Headers.Add("X-Seq-ApiKey", "bogus");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_ApiKeyViaQueryString_Accepted()
	{
		using var client = _factory.CreateClient();
		var body = """{"@t":"2026-04-19T10:00:00Z","@m":"via-query"}""";

		using var resp = await client.PostAsync(
			"/api/v1/ingest/clef?apiKey=test-key",
			new StringContent(body, Encoding.UTF8, "application/vnd.serilog.clef"));

		resp.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task Post_PartialBadBatch_Accepts_ValidEvents()
	{
		using var client = _factory.CreateClient();
		var body =
			"""{"@t":"2026-04-19T10:00:00Z","@m":"ok"}""" + "\n" +
			"not-json\n" +
			"""{"@t":"2026-04-19T10:00:02Z","@m":"ok2"}""";

		using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/clef")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/vnd.serilog.clef"),
		};
		req.Headers.Add("X-Seq-ApiKey", "test-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);

		await WaitForEventsAsync(expected: 2);
	}

	async Task WaitForEventsAsync(long expected)
	{
		var store = (ILogStore)_factory.Services.GetService(typeof(ILogStore))!;
		var ws = WorkspaceId.Parse("integration");
		var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var c = await store.CountAsync(ws, new LogQuery(PageSize: 1), CancellationToken.None);
			if (c >= expected)
				return;
			await Task.Delay(25);
		}
	}
}
