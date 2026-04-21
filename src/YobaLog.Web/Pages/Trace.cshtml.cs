using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Tracing;

namespace YobaLog.Web.Pages;

public sealed class TraceModel : PageModel
{
	readonly ISpanStore _spans;

	public TraceModel(ISpanStore spans)
	{
		_spans = spans;
	}

	public WorkspaceId Workspace { get; private set; }
	public string TraceId { get; private set; } = "";

	[BindProperty(SupportsGet = true, Name = "ws")]
	public string? WorkspaceSlug { get; set; }

	// Waterfall rows — each span flattened with its depth in the parent tree so the Razor
	// partial can set indent without walking the tree itself. Ordered by start time so
	// rendering is a straight foreach.
	public ImmutableArray<WaterfallRow> Rows { get; private set; } = [];

	// Anchors for width-normalization in the view (each bar's width = span.Duration / TraceDuration).
	public DateTimeOffset TraceStart { get; private set; }
	public TimeSpan TraceDuration { get; private set; }

	public string? NotFoundMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		TraceId = id;

		// Workspace resolution — explicit via ?ws= (links from /ws/{id} event rows can prefill it).
		// Fallback: $system, since self-emitted spans live there and that's the common debugging entry.
		var slug = string.IsNullOrWhiteSpace(WorkspaceSlug) ? WorkspaceId.System.Value : WorkspaceSlug;
		if (!WorkspaceId.TryParse(slug, out var ws))
		{
			NotFoundMessage = $"Unknown workspace: {slug}";
			return Page();
		}
		Workspace = ws;

		var spans = await _spans.GetByTraceIdAsync(ws, id, ct);
		if (spans.Count == 0)
		{
			NotFoundMessage = $"No spans for trace {id} in {ws.Value}";
			return Page();
		}

		// Compute depths via parent_span_id map. Root spans (ParentSpanId == null OR parent not in
		// this trace — cross-trace links aren't waterfall-rendered) get depth 0; every child is
		// parent.depth + 1. Parents that aren't in the fetched set get treated as roots (defensive:
		// span sampling / clock skew can drop a parent).
		var byId = spans.ToDictionary(s => s.SpanId, s => s, StringComparer.Ordinal);
		var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);

		int DepthOf(Span span)
		{
			if (depthCache.TryGetValue(span.SpanId, out var cached)) return cached;
			if (span.ParentSpanId is null || !byId.TryGetValue(span.ParentSpanId, out var parent))
			{
				depthCache[span.SpanId] = 0;
				return 0;
			}
			var d = DepthOf(parent) + 1;
			depthCache[span.SpanId] = d;
			return d;
		}

		TraceStart = spans.Min(s => s.StartTime);
		var traceEnd = spans.Max(s => s.StartTime + s.Duration);
		TraceDuration = traceEnd - TraceStart;
		if (TraceDuration <= TimeSpan.Zero)
			TraceDuration = TimeSpan.FromMilliseconds(1); // avoid divide-by-zero in width calc

		var builder = ImmutableArray.CreateBuilder<WaterfallRow>(spans.Count);
		foreach (var s in spans)
		{
			var depth = DepthOf(s);
			var offset = s.StartTime - TraceStart;
			var offsetPct = Math.Clamp(offset.TotalMilliseconds / TraceDuration.TotalMilliseconds * 100.0, 0.0, 100.0);
			// Minimum width so zero-duration spans are still visible as a tick.
			var widthPct = Math.Max(s.Duration.TotalMilliseconds / TraceDuration.TotalMilliseconds * 100.0, 0.5);
			builder.Add(new WaterfallRow(s, depth, offsetPct, widthPct));
		}
		Rows = builder.ToImmutable();
		return Page();
	}
}

public sealed record WaterfallRow(Span Span, int Depth, double OffsetPct, double WidthPct);
