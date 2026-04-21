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
		using var activity = workspaceId.IsSystem ? null : ActivitySources.StorageTraces.StartActivity("traces.append.batch");
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
		using var activity = workspaceId.IsSystem ? null : ActivitySources.StorageTraces.StartActivity("traces.get.by_trace_id");
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

	public async IAsyncEnumerable<Span> QueryKqlAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(kql);

		using var activity = workspaceId.IsSystem ? null : ActivitySources.StorageTraces.StartActivity("traces.query.kql");
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
		using var activity = workspaceId.IsSystem ? null : ActivitySources.StorageTraces.StartActivity("traces.delete.older_than");
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
