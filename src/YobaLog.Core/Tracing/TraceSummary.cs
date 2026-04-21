using Kusto.Language;

namespace YobaLog.Core.Tracing;

// Aggregate view of a trace for the listing page — one row per TraceId.
// Cheap to compute via SQL GROUP BY; root-span name comes from the span with
// ParentSpanId IS NULL (or the earliest span if no clear root, e.g. cross-process
// trace where the parent is in another service's span store).
public sealed record TraceSummary(
	string TraceId,
	string RootName,
	DateTimeOffset StartTime,
	TimeSpan Duration,
	int SpanCount,
	SpanStatusCode WorstStatus);

// Parameters for listing-page queries. Two axes beyond pagination:
//   - Filter: optional `spans | where …` KustoCode. Applied to individual spans
//     before GROUP BY — a trace appears if AT LEAST ONE of its spans matches.
//     Matches KQL-over-events UX on the workspace page.
//   - SinceStartUnixNs: returns only traces that started strictly after the given
//     unix-ns. Used by the incremental auto-refresh flow (htmx polls with the
//     topmost row's StartUnixNs and gets only the new ones prepended).
public sealed record TracesQuery(
	int PageSize = 50,
	long? CursorStartUnixNs = null,
	string? CursorTraceId = null,
	long? SinceStartUnixNs = null,
	KustoCode? Filter = null);
