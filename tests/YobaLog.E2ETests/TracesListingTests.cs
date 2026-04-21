using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Tracing;
using static YobaLog.E2ETests.Infrastructure.SeedHelper;

namespace YobaLog.E2ETests;

// E2E for the Phase H.4 traces listing UI at /ws/{id}/traces. Seeds three traces with
// different roots / statuses, navigates the page, asserts rows + click → /trace/{id}.
[Collection(nameof(UiCollection))]
public sealed class TracesListingTests
{
	readonly WebAppFixture _app;

	public TracesListingTests(WebAppFixture app) => _app = app;

	static Span MakeSpan(
		string spanId,
		string traceId,
		string name,
		DateTimeOffset start,
		TimeSpan duration,
		string? parentSpanId = null,
		SpanStatusCode status = SpanStatusCode.Unset)
	{
		return new Span(
			SpanId: spanId,
			TraceId: traceId,
			ParentSpanId: parentSpanId,
			Name: name,
			Kind: SpanKind.Internal,
			StartTime: start,
			Duration: duration,
			Status: status,
			StatusDescription: status == SpanStatusCode.Error ? "boom" : null,
			Attributes: ImmutableDictionary<string, JsonElement>.Empty,
			Events: [],
			Links: []);
	}

	[Fact]
	public async Task Listing_Renders_Rows_And_Click_Navigates_To_Waterfall()
	{
		var ws = FreshWorkspace("traces-list");
		await _app.SeedAsync(ws);

		var spans = _app.Services.GetRequiredService<ISpanStore>();
		var wsId = WorkspaceId.Parse(ws);
		await spans.CreateWorkspaceAsync(wsId, CancellationToken.None);

		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);
		var traceA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
		var traceB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1";

		await spans.AppendBatchAsync(wsId,
			[
				MakeSpan("a000000000000001", traceA, "GET /api/users", t0, TimeSpan.FromMilliseconds(150), status: SpanStatusCode.Ok),
				MakeSpan("a000000000000002", traceA, "db.users.select", t0.AddMilliseconds(20), TimeSpan.FromMilliseconds(80), parentSpanId: "a000000000000001"),
				MakeSpan("b000000000000001", traceB, "POST /api/login", t0.AddSeconds(1), TimeSpan.FromMilliseconds(50), status: SpanStatusCode.Error),
			], CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/ws/{ws}/traces");

		// Two trace rows, newest first.
		var rows = page.GetByTestId("trace-row");
		await Expect(rows).ToHaveCountAsync(2);
		await Expect(page.GetByTestId("traces-count")).ToHaveTextAsync("2 traces");

		// Trace B (later, error) is on top.
		var roots = page.GetByTestId("trace-root-name");
		await Expect(roots.Nth(0)).ToHaveTextAsync("POST /api/login");
		await Expect(roots.Nth(1)).ToHaveTextAsync("GET /api/users");

		// Click the link on the first row → waterfall page for trace B.
		await page.GetByTestId("trace-link").Nth(0).ClickAsync();
		await page.WaitForURLAsync($"**/trace/{traceB}**");
		await Expect(page.GetByTestId("trace-id")).ToHaveTextAsync(traceB);

		// Breadcrumb "traces" takes us back to the listing (not to /ws/{id} — that's what the
		// workspace-name crumb does).
		await page.GetByTestId("trace-back-traces").ClickAsync();
		await page.WaitForURLAsync($"**/ws/{ws}/traces");
	}

	[Fact]
	public async Task Listing_Empty_Workspace_Shows_Empty_State()
	{
		var ws = FreshWorkspace("traces-empty");
		await _app.SeedAsync(ws);
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		await spans.CreateWorkspaceAsync(WorkspaceId.Parse(ws), CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/ws/{ws}/traces");
		await Expect(page.GetByTestId("traces-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Workspace_Page_Has_Traces_Link()
	{
		var ws = FreshWorkspace("traces-nav");
		await _app.SeedAsync(ws);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/ws/{ws}");
		await Expect(page.GetByTestId("workspace-traces-link")).ToBeVisibleAsync();
		await page.GetByTestId("workspace-traces-link").ClickAsync();
		await page.WaitForURLAsync($"**/ws/{ws}/traces");
	}

	[Fact]
	public async Task Kql_Filter_Narrows_Visible_Rows()
	{
		var ws = FreshWorkspace("traces-kql");
		await _app.SeedAsync(ws);
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		var wsId = WorkspaceId.Parse(ws);
		await spans.CreateWorkspaceAsync(wsId, CancellationToken.None);

		var t0 = new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero);
		await spans.AppendBatchAsync(wsId,
			[
				MakeSpan("a0000000aaaaaaaa", "trace-a".PadRight(32, 'f'), "GET /users", t0, TimeSpan.FromMilliseconds(50)),
				MakeSpan("b0000000bbbbbbbb", "trace-b".PadRight(32, 'f'), "POST /login", t0.AddSeconds(1), TimeSpan.FromMilliseconds(80)),
				MakeSpan("c0000000cccccccc", "trace-c".PadRight(32, 'f'), "GET /error", t0.AddSeconds(2), TimeSpan.FromMilliseconds(20)),
			], CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		await page.GotoAsync($"/ws/{ws}/traces");
		await Expect(page.GetByTestId("trace-row")).ToHaveCountAsync(3);

		// Type a span-level filter and apply — only the /error trace survives.
		await page.GetByTestId("traces-kql").FillAsync("spans | where Name contains 'error'");
		await page.GetByTestId("traces-apply").ClickAsync();
		await Expect(page.GetByTestId("trace-row")).ToHaveCountAsync(1);
		await Expect(page.GetByTestId("trace-root-name")).ToHaveTextAsync("GET /error");
	}

	[Fact]
	public async Task Kql_Parse_Error_Shows_Alert()
	{
		var ws = FreshWorkspace("traces-kql-bad");
		await _app.SeedAsync(ws);
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		await spans.CreateWorkspaceAsync(WorkspaceId.Parse(ws), CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();

		// `events` is not the right table — KqlSpansTransformer requires `spans`.
		await page.GotoAsync($"/ws/{ws}/traces?kql=events | take 10");
		await Expect(page.GetByTestId("traces-kql-error")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Tbody_Has_Poll_Attributes_For_Auto_Refresh()
	{
		// The every-5s incremental refresh uses htmx attributes on #traces-body. Rather
		// than waiting 6+s in E2E (flaky + slow), assert the markup is wired correctly —
		// htmx itself is well-tested upstream.
		var ws = FreshWorkspace("traces-poll");
		await _app.SeedAsync(ws);
		var spans = _app.Services.GetRequiredService<ISpanStore>();
		var wsId = WorkspaceId.Parse(ws);
		await spans.CreateWorkspaceAsync(wsId, CancellationToken.None);
		await spans.AppendBatchAsync(wsId,
			[MakeSpan("p0000000poll0001", "trace-p".PadRight(32, 'f'), "GET /poll", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10))],
			CancellationToken.None);

		await using var ctx = await _app.NewContextAsync();
		var page = await ctx.NewPageAsync();
		await page.GotoAsync($"/ws/{ws}/traces");

		var tbody = page.Locator("#traces-body");
		await Expect(tbody).ToHaveAttributeAsync("hx-trigger", "every 5s");
		await Expect(tbody).ToHaveAttributeAsync("hx-swap", "afterbegin");
		await Expect(tbody).ToHaveAttributeAsync("hx-get", $"/ws/{ws}/traces");

		// Row carries StartUnixNs so the JS `hx-vals` snippet can pick the topmost value.
		await Expect(page.GetByTestId("trace-row").Nth(0)).ToHaveAttributeAsync("data-start-unix-ns", new System.Text.RegularExpressions.Regex("^[0-9]+$"));
	}
}
