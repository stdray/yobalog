using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace YobaLog.Tests.Web;

// Endpoint coverage for Phase H.2 OTLP Traces — both aliases respond 201 identically
// (decision-log Rule 2 parity with /v1/logs), auth uses X-Seq-ApiKey header or ?apiKey=,
// garbage → 400.
public sealed class OtlpTracesEndpointTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly WebApplicationFactory<Program> _factory;

	public OtlpTracesEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-otlp-tr-" + Guid.NewGuid().ToString("N")[..8]);
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
						["ApiKeys:Keys:0:Token"] = "traces-key",
						["ApiKeys:Keys:0:Workspace"] = "traces-ws",
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

	static byte[] BuildRequest(string name = "test-op", params (string Key, string Value)[] resourceAttrs)
	{
		var req = new ExportTraceServiceRequest();
		var resource = new Resource();
		foreach (var (k, v) in resourceAttrs)
			resource.Attributes.Add(new KeyValue { Key = k, Value = new AnyValue { StringValue = v } });

		var span = new ProtoSpan
		{
			TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()),
			SpanId = ByteString.CopyFrom(Enumerable.Range(0x10, 8).Select(i => (byte)i).ToArray()),
			Name = name,
			Kind = ProtoSpan.Types.SpanKind.Internal,
			StartTimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL,
			EndTimeUnixNano = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 50UL) * 1_000_000UL,
		};

		var scope = new ScopeSpans();
		scope.Spans.Add(span);
		var rs = new ResourceSpans { Resource = resource };
		rs.ScopeSpans.Add(scope);
		req.ResourceSpans.Add(rs);
		return req.ToByteArray();
	}

	[Theory]
	[InlineData("/ingest/otlp/v1/traces")]
	[InlineData("/v1/traces")]
	public async Task Post_ValidProtobuf_To_EitherPath_Yields201_AndStoresSpans(string path)
	{
		using var client = _factory.CreateClient();
		var payload = BuildRequest("endpoint-roundtrip", ("service.name", "test-emitter"));

		using var req = new HttpRequestMessage(HttpMethod.Post, path)
		{
			Content = new ByteArrayContent(payload),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		req.Headers.Add("X-Seq-ApiKey", "traces-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created,
			$"decision-log Rule 2 — both paths alias one handler, so identical 201 expected on {path}");

		var spans = (ISpanStore)_factory.Services.GetService(typeof(ISpanStore))!;
		var ws = WorkspaceId.Parse("traces-ws");

		var count = await spans.CountAsync(ws, CancellationToken.None);
		count.Should().BeGreaterThanOrEqualTo(1);
	}

	[Fact]
	public async Task Post_MissingKey_ReturnsUnauthorized()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/traces")
		{
			Content = new ByteArrayContent(BuildRequest()),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_WrongKey_ReturnsUnauthorized()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/traces")
		{
			Content = new ByteArrayContent(BuildRequest()),
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
		using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/otlp/v1/traces")
		{
			Content = new ByteArrayContent([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
		req.Headers.Add("X-Seq-ApiKey", "traces-key");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_ApiKey_ViaQueryString_Accepted()
	{
		using var client = _factory.CreateClient();
		using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/traces?apiKey=traces-key")
		{
			Content = new ByteArrayContent(BuildRequest("query-auth-ok")),
		};
		req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

		using var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Created);
	}
}
