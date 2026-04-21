using System.Collections.Concurrent;
using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Kql;
using YobaLog.Core.Observability;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Tracing.Sqlite;

// Per-workspace {workspace}.traces.db — a separate SQLite file from .logs.db/.meta.db.
// Storage isolation is deliberate (decision-log 2026-04-21 option b2): SQLite's per-file
// writer-lock means spans in the same file as events would let trace-ingest bursts block
// log-read latency (measured 66× penalty under mixed workload), and it enables asymmetric
// retention (logs 30d / spans 7d, typical ask).
//
// Reuses SqliteLogStoreOptions.DataDirectory — traces live alongside logs on disk, just
// under a different file-suffix. If we ever want to separate the disk locations (fast SSD
// for traces, big-cheap-disk for logs), that's a new option property; not today.
public sealed class SqliteSpanStore : ISpanStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();

	public SqliteSpanStore(IOptions<SqliteLogStoreOptions> options)
	{
		_options = options.Value;
	}

	string PathFor(WorkspaceId ws) =>
		_pathCache.GetOrAdd(ws, w => Path.Combine(_options.DataDirectory, $"{w.Value}.traces.db"));

	DataConnection Open(WorkspaceId ws) =>
		SQLiteTools.CreateDataConnection($"Data Source={PathFor(ws)};Cache=Shared");

	public ValueTask InitializeAsync(CancellationToken ct)
	{
		// Per-workspace schema is created on first CreateWorkspaceAsync; no global init needed.
		// Kept on the interface for symmetry with the meta-stores pattern.
		Directory.CreateDirectory(_options.DataDirectory);
		return ValueTask.CompletedTask;
	}

	public async ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using var db = Open(workspaceId);
		await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
		await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
		foreach (var stmt in SpansSchema.AllStatements)
			await db.ExecuteAsync(stmt, ct).ConfigureAwait(false);
	}

	public async ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		SqliteConnection.ClearAllPools();
		GC.Collect();
		GC.WaitForPendingFinalizers();

		var path = PathFor(workspaceId);
		if (File.Exists(path))
			File.Delete(path);
		foreach (var suffix in (ReadOnlySpan<string>)["-wal", "-shm", "-journal"])
		{
			var extra = path + suffix;
			if (File.Exists(extra))
				File.Delete(extra);
		}
		_pathCache.TryRemove(workspaceId, out _);
		await ValueTask.CompletedTask.ConfigureAwait(false);
	}

	public async ValueTask AppendBatchAsync(
		WorkspaceId workspaceId,
		IReadOnlyList<Span> batch,
		CancellationToken ct)
	{
		if (batch.Count == 0)
			return;

		// $system writes skip instrumentation — same recursion-safety rule as SqliteLogStore:
		// the span exporter writes to $system.traces.db, so tracing that write would fire
		// another span which would be queued for export which would ... (etc).
		using var activity = workspaceId.IsSystem ? null : ActivitySources.Storage.StartActivity("storage.append.batch");
		activity?.SetTag("storage.kind", "traces");
		activity?.SetTag("workspace", workspaceId.Value);
		activity?.SetTag("batch.size", batch.Count);

		await using var db = Open(workspaceId);
		// BulkCopyAsync with conflict resolution = Ignore so duplicate SpanIds (replayed batches)
		// don't tombstone the whole operation. Span IDs are 128-bit random; collisions that
		// aren't replays are essentially impossible.
		await db.GetTable<SpanRecord>()
			.BulkCopyAsync(new BulkCopyOptions { KeepIdentity = true }, batch.Select(SpanRecord.FromSpan), ct)
			.ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<Span>> GetByTraceIdAsync(
		WorkspaceId workspaceId,
		string traceId,
		CancellationToken ct)
	{
		using var activity = workspaceId.IsSystem ? null : ActivitySources.Storage.StartActivity("storage.get.by_trace_id");
		activity?.SetTag("storage.kind", "traces");
		activity?.SetTag("workspace", workspaceId.Value);
		activity?.SetTag("trace_id", traceId);

		await using var db = Open(workspaceId);
		// (TraceId, StartUnixNs) composite index serves both the WHERE and the ORDER BY.
		var records = await db.GetTable<SpanRecord>()
			.Where(r => r.TraceId == traceId)
			.OrderBy(r => r.StartUnixNs)
			.ToListAsync(ct)
			.ConfigureAwait(false);

		activity?.SetTag("span.count", records.Count);
		return [.. records.Select(r => r.ToSpan())];
	}

	public async ValueTask<IReadOnlyList<TraceSummary>> ListRecentTracesAsync(
		WorkspaceId workspaceId,
		TracesQuery query,
		CancellationToken ct)
	{
		using var activity = workspaceId.IsSystem ? null : ActivitySources.Storage.StartActivity("storage.list.traces");
		activity?.SetTag("storage.kind", "traces");
		activity?.SetTag("workspace", workspaceId.Value);
		activity?.SetTag("page.size", query.PageSize);

		await using var db = Open(workspaceId);

		// Optional span-level filter via KQL (e.g. `spans | where Name contains "error"`).
		// A trace surfaces in the list if AT LEAST ONE of its spans matches the filter —
		// natural "show traces containing errors" semantics. `KqlSpansTransformer.Apply`
		// returns an IQueryable that linq2db can still GroupBy on.
		var spans = db.GetTable<SpanRecord>().AsQueryable();
		if (query.Filter is not null)
			spans = KqlSpansTransformer.Apply(spans, query.Filter);

		// Aggregate per TraceId: earliest start, latest end, count, worst status code.
		// Cursor: keep only traces whose (StartUnixNs, TraceId) is strictly less than the
		// cursor (newer-first ordering: descending by StartUnixNs, then descending by TraceId
		// as a stable tiebreaker for ties at the nanosecond).
		var aggregates = spans
			.GroupBy(s => s.TraceId)
			.Select(g => new
			{
				TraceId = g.Key,
				StartUnixNs = g.Min(s => s.StartUnixNs),
				EndUnixNs = g.Max(s => s.EndUnixNs),
				SpanCount = g.Count(),
				WorstStatus = g.Max(s => s.StatusCode),
			});

		if (query.CursorStartUnixNs is long cursorStart && query.CursorTraceId is string cursorId)
		{
			aggregates = aggregates.Where(a =>
				a.StartUnixNs < cursorStart
				|| (a.StartUnixNs == cursorStart && string.Compare(a.TraceId, cursorId, StringComparison.Ordinal) < 0));
		}

		if (query.SinceStartUnixNs is long sinceNs)
		{
			// Incremental-refresh path: return only traces that started strictly after the
			// client's most-recent top row. Client cursor is the StartUnixNs of the topmost
			// visible <tr>; server returns only newer ones for hx-swap="afterbegin".
			aggregates = aggregates.Where(a => a.StartUnixNs > sinceNs);
		}

		var pageRows = await aggregates
			.OrderByDescending(a => a.StartUnixNs)
			.ThenByDescending(a => a.TraceId)
			.Take(query.PageSize)
			.ToListAsync(ct)
			.ConfigureAwait(false);

		if (pageRows.Count == 0)
			return [];

		// Resolve root-span name for each TraceId in one round trip. ParentSpanId IS NULL =
		// the root span of this trace (or the earliest span if no clear root, e.g. detached
		// child branch). Take the root by min(StartUnixNs) within the trace as the tiebreaker.
		var traceIds = pageRows.Select(r => r.TraceId).ToList();
		var rootByTraceId = await db.GetTable<SpanRecord>()
			.Where(s => traceIds.Contains(s.TraceId))
			.GroupBy(s => s.TraceId)
			.Select(g => new
			{
				TraceId = g.Key,
				RootName = g.OrderBy(s => s.ParentSpanId == null ? 0 : 1)
					.ThenBy(s => s.StartUnixNs)
					.Select(s => s.Name)
					.First(),
			})
			.ToDictionaryAsync(r => r.TraceId, r => r.RootName, ct)
			.ConfigureAwait(false);

		activity?.SetTag("result.count", pageRows.Count);
		return [.. pageRows.Select(r => new TraceSummary(
			TraceId: r.TraceId,
			RootName: rootByTraceId.TryGetValue(r.TraceId, out var name) ? name : "(unknown)",
			StartTime: FromUnixNanos(r.StartUnixNs),
			Duration: FromUnixNanos(r.EndUnixNs) - FromUnixNanos(r.StartUnixNs),
			SpanCount: r.SpanCount,
			WorstStatus: (SpanStatusCode)r.WorstStatus))];
	}

	static DateTimeOffset FromUnixNanos(long unixNs)
	{
		var ms = unixNs / 1_000_000L;
		var subMs = unixNs % 1_000_000L;
		return DateTimeOffset.FromUnixTimeMilliseconds(ms).AddTicks(subMs / 100L);
	}

	public async IAsyncEnumerable<Span> QueryKqlAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(kql);

		using var activity = workspaceId.IsSystem ? null : ActivitySources.Storage.StartActivity("storage.query.kql");
		activity?.SetTag("storage.kind", "traces");
		activity?.SetTag("workspace", workspaceId.Value);

		await using var db = Open(workspaceId);
		var source = db.GetTable<SpanRecord>().AsQueryable();
		var translated = KqlSpansTransformer.Apply(source, kql);

		await foreach (var r in translated.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return r.ToSpan();
	}

	public Task<KqlResult> QueryKqlResultAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(kql);
		_ = workspaceId;
		// Shape-changing operators (project / extend / summarize / count) over the spans
		// target are deferred — the H.3 transformer handles filter-sort-limit only.
		return Task.FromException<KqlResult>(
			new NotSupportedException("project/extend/summarize/count on spans target deferred; use QueryKqlAsync + filter/sort/take"));
	}

	public async ValueTask<long> CountAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		return await db.GetTable<SpanRecord>().LongCountAsync(ct).ConfigureAwait(false);
	}

	public async ValueTask<long> DeleteOlderThanAsync(
		WorkspaceId workspaceId,
		DateTimeOffset cutoff,
		CancellationToken ct)
	{
		using var activity = workspaceId.IsSystem ? null : ActivitySources.Storage.StartActivity("storage.delete.older_than");
		activity?.SetTag("storage.kind", "traces");
		activity?.SetTag("workspace", workspaceId.Value);
		activity?.SetTag("cutoff", cutoff.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

		var cutoffUnixNs = cutoff.ToUnixTimeMilliseconds() * 1_000_000L;
		await using var db = Open(workspaceId);
		var deleted = await db.GetTable<SpanRecord>()
			.Where(r => r.StartUnixNs < cutoffUnixNs)
			.DeleteAsync(ct)
			.ConfigureAwait(false);
		activity?.SetTag("deleted", deleted);
		return deleted;
	}
}
