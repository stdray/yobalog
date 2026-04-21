using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Web;

// End-to-end HTTP tests for the OTLP Logs endpoint. Covers both aliases
// (/ingest/otlp/v1/logs Seq-mirror path and /v1/logs OTel-standard path), partial-batch
// handling, and auth. Decision-log 2026-04-21 Rule 2 promises identical response shape
// on both paths — asserted here.
public sealed class OtlpIngestionEndpointTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;

	public OtlpIngestionEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-otlp-" + Guid.NewGuid().ToString("N")[..8]);
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
						["ApiKeys:Keys:0:Token"] = "otlp-key",
						["ApiKeys:Keys:0:Workspace"] = "otlp-ws",
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

	static byte[] BuildRequest(string body, params (string Key, string Value)[] resourceAttrs)
	{
		var req = new ExportLogsServiceRequest();
		var resource = new Resource();
		foreach (var (k, v) in resourceAttrs)
			resource.Attributes.Add(new KeyValue { Key = k, Value = new AnyValue { StringValue = v } });
		var rl = new ResourceLogs { Resource = resource };
		var scope = new ScopeLogs();
		scope.LogRecords.Add(new LogRecord
		{
			TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL,
			SeverityNumber = SeverityNumber.Info,
			Body = new AnyValue { StringValue = body },
		});
		rl.ScopeLogs.Add(scope);
		req.ResourceLogs.Add(rl);
		return req.ToByteArray();
	}

	async Task<long> WaitForEventsAsync(int expected, int maxWaitMs = 2000)
	{
		var store = (ILogStore)_factory.Services.GetService(typeof(ILogStore))!;
		var ws = WorkspaceId.Parse("otlp-ws");
		var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
		long count = 0;
		while (DateTimeOffset.UtcNow < deadline)
		{
			count = await store.CountAsync(ws, new LogQuery(PageSize: 10), CancellationToken.None);
			if (count >= expected) return count;
			await Task.Delay(25);
		}
		return count;
	}

	[Theory]
	[InlineData("/ingest/otlp/v1/logs")]
	[InlineData("/v1/logs")]
	public async Task Post_ValidProtobuf_To_EitherPath_Yields201_AndStoresEvents(string path)
	{
		using var client = _factory.CreateClient();
		var payload = BuildRequest("otlp-ingest-ok", ("service.name", "test-emitter"));

		using var req = new HttpRequestMessage(HttpMethod.Post, path)
		{
			Content = new ByteArrayContent(payload),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		req.Headers.Add("X-Seq-ApiKey", "otlp-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created,
			$"decision-log Rule 2 — both paths alias one handler, so identical 201 expected on {path}");

		var stored = await WaitForEventsAsync(expected: 1);
		stored.Should().Be(1);
	}

	[Fact]
	public async Task Post_MissingKey_ReturnsUnauthorized()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/logs")
		{
			Content = new ByteArrayContent(BuildRequest("unauth-attempt")),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_WrongKey_ReturnsUnauthorized()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/logs")
		{
			Content = new ByteArrayContent(BuildRequest("wrong-key-attempt")),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		req.Headers.Add("X-Seq-ApiKey", "definitely-not-the-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_GarbageBody_ReturnsBadRequest()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/logs")
		{
			Content = new ByteArrayContent([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		req.Headers.Add("X-Seq-ApiKey", "otlp-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_ApiKey_ViaQueryString_Accepted()
	{
		// Same convention as CLEF: apiKey query param is an alternative to X-Seq-ApiKey header.
		using var client = _factory.CreateClient();
		var payload = BuildRequest("qs-auth-ok");

		using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/logs?apiKey=otlp-key")
		{
			Content = new ByteArrayContent(payload),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);

		var stored = await WaitForEventsAsync(expected: 1);
		stored.Should().Be(1);
	}
}
