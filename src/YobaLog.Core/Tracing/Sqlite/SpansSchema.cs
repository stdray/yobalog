namespace YobaLog.Core.Tracing.Sqlite;

static class SpansSchema
{
	public const string CreateSpansTable = """
		CREATE TABLE IF NOT EXISTS Spans (
			SpanId            TEXT    PRIMARY KEY,
			TraceId           TEXT    NOT NULL,
			ParentSpanId      TEXT,
			Name              TEXT    NOT NULL,
			Kind              INTEGER NOT NULL,
			StartUnixNs       INTEGER NOT NULL,
			EndUnixNs         INTEGER NOT NULL,
			StatusCode        INTEGER NOT NULL,
			StatusDescription TEXT,
			AttributesJson    TEXT    NOT NULL DEFAULT '{}',
			EventsJson        TEXT    NOT NULL DEFAULT '[]',
			LinksJson         TEXT    NOT NULL DEFAULT '[]'
		);
		""";

	// Waterfall hot path: GET /trace/{id} fetches spans for a single trace in start-order.
	// Composite (TraceId, StartUnixNs) serves both the WHERE and the ORDER BY in one pass.
	public const string CreateTraceIdStartIndex =
		"CREATE INDEX IF NOT EXISTS ix_spans_trace_start ON Spans(TraceId, StartUnixNs);";

	// Retention sweep: DELETE FROM Spans WHERE StartUnixNs < cutoff. Without this it's a
	// full scan on every retention pass.
	public const string CreateStartIndex =
		"CREATE INDEX IF NOT EXISTS ix_spans_start ON Spans(StartUnixNs);";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateSpansTable,
		CreateTraceIdStartIndex,
		CreateStartIndex,
	];
}
