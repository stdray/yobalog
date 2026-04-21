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

// Cursor for newer-first pagination over traces. Composite (StartUnixNs, TraceId)
// — StartUnixNs alone isn't unique (multiple traces can start in the same nanosecond
// under load). Cursor encoded as opaque string by the page model; the store just
// needs the raw values.
public sealed record TracesQuery(
	int PageSize = 50,
	long? CursorStartUnixNs = null,
	string? CursorTraceId = null);
