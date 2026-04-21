using Kusto.Language;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage;

namespace YobaLog.Core.Tracing;

// Sibling of ILogStore scoped to spans. Storage is per-workspace {workspace}.traces.db —
// decision-log 2026-04-21 option (b2): separate file so span-ingest bursts don't block
// log-read latency through SQLite's shared writer-lock (measured 66× penalty in
// perf-baseline.md Tier 2 mixed-workload), and so span-retention can be asymmetric from
// log-retention (typical ask: "logs 30 days, traces 7").
//
// Contract mirrors ILogStore where it makes sense (KQL query, delete-older-than, create/
// drop workspace). GetByTraceIdAsync is a specialized hot path for the waterfall view —
// could be expressed as `spans | where TraceId == '...'` through KqlTransformer but
// that's indirection for no gain on a single-trace lookup.
public interface ISpanStore
{
	ValueTask InitializeAsync(CancellationToken ct);

	ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask AppendBatchAsync(
		WorkspaceId workspaceId,
		IReadOnlyList<Span> batch,
		CancellationToken ct);

	// Direct-SQL waterfall lookup. Returns spans ordered by StartTime ascending, materialized
	// in one shot (typical trace = dozens of spans, not streaming-scale).
	ValueTask<IReadOnlyList<Span>> GetByTraceIdAsync(
		WorkspaceId workspaceId,
		string traceId,
		CancellationToken ct);

	// Listing-page hot path: aggregate one row per trace_id, newest first. SQL GROUP BY
	// over the Spans table — at the volumes we expect (10k–100k spans) this is fast enough
	// without a precomputed summary table. If volume grows past that, the right move is a
	// trigger-maintained `TraceSummaries` table; not today.
	ValueTask<IReadOnlyList<TraceSummary>> ListRecentTracesAsync(
		WorkspaceId workspaceId,
		TracesQuery query,
		CancellationToken ct);

	IAsyncEnumerable<Span> QueryKqlAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		CancellationToken ct);

	Task<KqlResult> QueryKqlResultAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		CancellationToken ct);

	ValueTask<long> CountAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask<long> DeleteOlderThanAsync(
		WorkspaceId workspaceId,
		DateTimeOffset cutoff,
		CancellationToken ct);
}
