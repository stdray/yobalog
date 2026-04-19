using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using YobaLog.Core;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Storage;

namespace YobaLog.Tests.Sharing;

public sealed class ShareEndpointTests : IAsyncLifetime
{
	static readonly string[] DefaultColumns = [
		"Id", "Timestamp", "Level", "Message", "MessageTemplate", "Exception", "TraceId", "SpanId", "EventId",
	];

	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;

	public ShareEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-share-" + Guid.NewGuid().ToString("N")[..8]);
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
						["ApiKeys:Keys:0:Workspace"] = "sharedev",
						["Admin:Username"] = "admin",
						["Admin:Password"] = "pw",
						["Share:DefaultTtlHours"] = "1",
						["Share:MaxRows"] = "100",
					});
				});
			});
	}

	public Task InitializeAsync()
	{
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

	async Task<HttpClient> AuthedClientAsync()
	{
		var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = true,
		});
		using var login = await client.PostAsync(
			"/Login",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["Username"] = "admin",
				["Password"] = "pw",
			}));
		login.StatusCode.Should().Be(HttpStatusCode.Redirect);
		return client;
	}

	async Task SeedAsync(params LogEventCandidate[] events)
	{
		var store = (ILogStore)_factory.Services.GetService(typeof(ILogStore))!;
		await store.AppendBatchAsync(WorkspaceId.Parse("sharedev"), events, CancellationToken.None);
	}

	static LogEventCandidate Event(
		DateTimeOffset ts,
		string msg,
		string? traceId = null,
		ImmutableDictionary<string, JsonElement>? props = null) =>
		new(ts, LogLevel.Information, msg, msg, null, traceId, null, null,
			props ?? ImmutableDictionary<string, JsonElement>.Empty);

	[Fact]
	public async Task CreateShare_Then_FetchTsv()
	{
		await SeedAsync(
			Event(DateTimeOffset.UtcNow.AddMinutes(-2), "hello"),
			Event(DateTimeOffset.UtcNow.AddMinutes(-1), "world"));

		using var client = await AuthedClientAsync();
		using var resp = await client.PostAsJsonAsync("/api/ws/sharedev/share", new
		{
			kql = "events",
			ttlHours = 1,
			columns = DefaultColumns,
		});
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
		var url = body.GetProperty("url").GetString()!;
		url.Should().EndWith(".tsv");

		using var anon = _factory.CreateClient();
		using var tsvResp = await anon.GetAsync(new Uri(url).PathAndQuery);
		tsvResp.StatusCode.Should().Be(HttpStatusCode.OK);
		tsvResp.Content.Headers.ContentType!.MediaType.Should().Be("text/tab-separated-values");

		var tsv = await tsvResp.Content.ReadAsStringAsync();
		var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		lines.Should().HaveCount(3);
		lines[0].Should().StartWith("Id\tTimestamp\tLevel");
		tsv.Should().Contain("hello");
		tsv.Should().Contain("world");
	}

	[Fact]
	public async Task MaskMode_Hide_DropsColumn()
	{
		await SeedAsync(Event(DateTimeOffset.UtcNow.AddMinutes(-1), "m", traceId: "trace-abc"));

		using var client = await AuthedClientAsync();
		using var resp = await client.PostAsJsonAsync("/api/ws/sharedev/share", new
		{
			kql = "events",
			columns = DefaultColumns,
			modes = new Dictionary<string, string> { ["TraceId"] = "hide" },
		});
		var url = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;

		using var anon = _factory.CreateClient();
		var tsv = await anon.GetStringAsync(new Uri(url).PathAndQuery);
		tsv.Should().NotContain("trace-abc");
		tsv.Should().NotContain("\tTraceId\t");
	}

	[Fact]
	public async Task MaskMode_Mask_ReplacesWithHash()
	{
		await SeedAsync(Event(DateTimeOffset.UtcNow.AddMinutes(-1), "m", traceId: "trace-abc"));

		using var client = await AuthedClientAsync();
		using var resp = await client.PostAsJsonAsync("/api/ws/sharedev/share", new
		{
			kql = "events",
			columns = DefaultColumns,
			modes = new Dictionary<string, string> { ["TraceId"] = "mask" },
		});
		var url = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;

		using var anon = _factory.CreateClient();
		var tsv = await anon.GetStringAsync(new Uri(url).PathAndQuery);
		tsv.Should().NotContain("trace-abc");
		tsv.Should().Contain("traceid:");
	}

	[Fact]
	public async Task Property_Keys_AreFlatColumns()
	{
		using var doc = JsonDocument.Parse("""{"user":"alice","email":"a@b.com"}""");
		var props = ImmutableDictionary<string, JsonElement>.Empty
			.Add("user", doc.RootElement.GetProperty("user").Clone())
			.Add("email", doc.RootElement.GetProperty("email").Clone());
		await SeedAsync(Event(DateTimeOffset.UtcNow.AddMinutes(-1), "m", props: props));

		var columns = DefaultColumns.Concat(new[] { "user", "email" }).ToArray();

		using var client = await AuthedClientAsync();
		using var resp = await client.PostAsJsonAsync("/api/ws/sharedev/share", new
		{
			kql = "events",
			columns,
			modes = new Dictionary<string, string> { ["email"] = "mask" },
		});
		var url = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;

		using var anon = _factory.CreateClient();
		var tsv = await anon.GetStringAsync(new Uri(url).PathAndQuery);
		var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		lines[0].Should().Contain("user").And.Contain("email");
		lines[0].Should().NotContain("Properties.");
		tsv.Should().Contain("alice");           // "user" kept as-is
		tsv.Should().NotContain("a@b.com");      // "email" masked
		tsv.Should().Contain("email:");
	}

	[Fact]
	public async Task UnknownId_404()
	{
		using var anon = _factory.CreateClient();
		using var resp = await anon.GetAsync("/share/sharedev/ABCDEFGHIJKLMNOPQRSTUV.tsv");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Share_SavesPolicy_WhenRequested()
	{
		using var client = await AuthedClientAsync();
		using var resp = await client.PostAsJsonAsync("/api/ws/sharedev/share", new
		{
			kql = "events",
			columns = DefaultColumns,
			modes = new Dictionary<string, string> { ["TraceId"] = "mask" },
			savePolicy = true,
		});
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var store = (YobaLog.Core.Sharing.IFieldMaskingPolicyStore)_factory.Services.GetService(
			typeof(YobaLog.Core.Sharing.IFieldMaskingPolicyStore))!;
		var policy = await store.GetAsync(WorkspaceId.Parse("sharedev"), CancellationToken.None);
		policy.Modes.Should().ContainKey("TraceId");
		policy.Modes["TraceId"].Should().Be(YobaLog.Core.Sharing.MaskMode.Mask);
	}
}
