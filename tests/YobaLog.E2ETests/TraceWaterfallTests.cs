using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Tracing;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// E2E for the Phase H.3 waterfall UI at /trace/{id}. Seeds a small 3-span trace via
// ISpanStore.AppendBatchAsync, navigates the page, asserts rows + expand click.
[Collection(nameof(UiCollection))]
public sealed class TraceWaterfallTests
{
	readonly WebAppFixture _app;

	public TraceWaterfallTests(WebAppFixture app) => _app = app;

	static Span MakeSpan(
		string spanId,
		string traceId,
		string name,
		DateTimeOffset start,
		TimeSpan duration,
		string? parentSpanId = null,
		SpanKind kind = SpanKind.Internal,
		SpanStatusCode status = SpanStatusCode.Unset)
	{
		return new Span(
			SpanId: spanId,
			TraceId: traceId,
			ParentSpanId: parentSpanId,
			Name: name,
			Kind: kind,
			StartTime: start,
			Duration: duration,
			Status: status,
			StatusDescription: status == SpanStatusCode.Error ? "boom" : null,
			Attributes: ImmutableDictionary<string, JsonElement>.Empty,
			Events: [],
			Links: []);
	}

	[Fact]
	public async Task Waterfall_Renders_All_Spans_With_ExpandableDetails()
	{
		var ws = FreshWorkspace("waterfall");
		await _app.SeedAsync(ws); // init + empty event set so the workspace is registered

		// Workspaces created via SeedAsync hit ILogStore but not ISpanStore — do that ourselves
		// since SeedHelper is log-shaped.
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		var wsId = WorkspaceId.Parse(ws);
		await spans.CreateWorkspaceAsync(wsId, CancellationToken.None);

		const string traceId = "0102030405060708090a0b0c0d0e0f00";
		var t0 = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

		await spans.AppendBatchAsync(wsId,
			[
				MakeSpan("aaaaaaaaaaaaaaaa", traceId, "root.request", t0, TimeSpan.FromMilliseconds(500),
					kind: SpanKind.Server, status: SpanStatusCode.Ok),
				MakeSpan("bbbbbbbbbbbbbbbb", traceId, "db.query", t0.AddMilliseconds(100),
					TimeSpan.FromMilliseconds(80),
					parentSpanId: "aaaaaaaaaaaaaaaa", kind: SpanKind.Client, status: SpanStatusCode.Ok),
				MakeSpan("cccccccccccccccc", traceId, "cache.error", t0.AddMilliseconds(250),
					TimeSpan.FromMilliseconds(10),
					parentSpanId: "aaaaaaaaaaaaaaaa", status: SpanStatusCode.Error),
			], CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/trace/{traceId}?ws={ws}");

		// Shell chrome shows the canonical trace id + span count + duration.
		await Expect(page.GetByTestId("trace-id")).ToHaveTextAsync(traceId);
		await Expect(page.GetByTestId("trace-span-count")).ToHaveTextAsync("3 spans");
		await Expect(page.GetByTestId("trace-duration")).ToContainTextAsync("ms");

		// Three waterfall rows rendered, names from the seed data.
		var rows = page.GetByTestId("waterfall-row");
		await Expect(rows).ToHaveCountAsync(3);
		var names = page.GetByTestId("waterfall-span-name");
		await Expect(names.Nth(0)).ToHaveTextAsync("root.request");
		await Expect(names.Nth(1)).ToHaveTextAsync("db.query");
		await Expect(names.Nth(2)).ToHaveTextAsync("cache.error");

		// Detail rows exist but are hidden by default.
		var details = page.GetByTestId("waterfall-details");
		await Expect(details).ToHaveCountAsync(3);
		await Expect(details.Nth(0)).ToBeHiddenAsync();

		// Click the first row — details row becomes visible.
		await rows.Nth(0).ClickAsync();
		await Expect(details.Nth(0)).ToBeVisibleAsync();
		await Expect(details.Nth(0)).ToContainTextAsync("aaaaaaaaaaaaaaaa");
	}

	[Fact]
	public async Task Waterfall_UnknownTrace_ShowsNotFoundAlert()
	{
		var ws = FreshWorkspace("waterfall-empty");
		await _app.SeedAsync(ws);
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		await spans.CreateWorkspaceAsync(WorkspaceId.Parse(ws), CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/trace/deadbeefdeadbeefdeadbeefdeadbeef?ws={ws}");
		await Expect(page.GetByTestId("trace-not-found")).ToBeVisibleAsync();
	}
}
