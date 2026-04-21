using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YobaLog.Core.Kql;

namespace YobaLog.Core.Storage.Sqlite;

public sealed class SqliteLogStore : ILogStore
{
	readonly SqliteLogStoreOptions _options;
	readonly ConcurrentDictionary<WorkspaceId, string> _pathCache = new();

	public SqliteLogStore(IOptions<SqliteLogStoreOptions> options)
	{
		_options = options.Value;
	}

	public async IAsyncEnumerable<LogEvent> QueryKqlAsync(
		WorkspaceId workspaceId,
		KustoCode kql,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var source = db.GetTable<EventRecord>().AsQueryable();
		var translated = KqlTransformer.Apply(source, kql);

		await foreach (var r in translated.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return ToLogEvent(r);
	}

	// Scan window for property-key discovery — newest N events parsed in C#. Good enough for MVP;
	// switch to a `json_each` SQL when the key catalog grows beyond what fits in one pass.
	const int PropertyKeyScanSize = 500;

	public async Task<IReadOnlyList<string>> GetPropertyKeysAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var table = db.GetTable<EventRecord>();
		var recent = table.OrderByDescending(e => e.Id).Take(PropertyKeyScanSize).Select(e => e.PropertiesJson);

		var keys = new HashSet<string>(StringComparer.Ordinal);
		await foreach (var json in recent.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
		{
			if (string.IsNullOrEmpty(json) || json == "{}")
				continue;
			try
			{
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.ValueKind != JsonValueKind.Object)
					continue;
				foreach (var prop in doc.RootElement.EnumerateObject())
					keys.Add(prop.Name);
			}
			catch (JsonException)
			{
			}
		}
		return [.. keys.OrderBy(k => k, StringComparer.Ordinal)];
	}

	// Hard cap on materialized rows for KqlResult — guards against unbounded 'project' / 'extend' output
	// in the viewer path. Aggregate ops (count / summarize) produce small results and never hit this.
	const int MaxMaterializedRows = 10_000;

	public async Task<KqlResult> QueryKqlResultAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var source = db.GetTable<EventRecord>().AsQueryable();
		var result = KqlTransformer.Execute(source, kql);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows.WithCancellation(ct).ConfigureAwait(false))
		{
			if (rows.Count >= MaxMaterializedRows)
				break;
			rows.Add(row);
		}
		return new KqlResult(result.Columns, ToAsyncEnumerable(rows));
	}

	static async IAsyncEnumerable<object?[]> ToAsyncEnumerable(IReadOnlyList<object?[]> rows)
	{
		foreach (var r in rows)
			yield return r;
		await Task.CompletedTask.ConfigureAwait(false);
	}

	string PathFor(WorkspaceId ws) =>
		_pathCache.GetOrAdd(ws, w => Path.Combine(_options.DataDirectory, $"{w.Value}.logs.db"));

	DataConnection Open(WorkspaceId ws) =>
		SQLiteTools.CreateDataConnection($"Data Source={PathFor(ws)};Cache=Shared");

	public async ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, WorkspaceSchema schema, CancellationToken ct)
	{
		Directory.CreateDirectory(_options.DataDirectory);

		await using var db = Open(workspaceId);
		await db.ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
		await db.ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
		foreach (var stmt in SqliteSchema.AllStatements)
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
		IReadOnlyList<LogEventCandidate> batch,
		CancellationToken ct)
	{
		if (batch.Count == 0)
			return;

		await using var db = Open(workspaceId);
		await db.GetTable<EventRecord>()
			.BulkCopyAsync(batch.Select(EventRecord.FromCandidate), ct)
			.ConfigureAwait(false);
	}

	public async IAsyncEnumerable<LogEvent> QueryAsync(
		WorkspaceId workspaceId,
		LogQuery query,
		[EnumeratorCancellation] CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var q = BuildQuery(db, query);
		q = q.OrderByDescending(e => e.TimestampMs).ThenByDescending(e => e.Id).Take(query.PageSize);

		await foreach (var r in q.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return ToLogEvent(r);
	}

	public async ValueTask<long> CountAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var q = BuildQuery(db, query);
		return await q.LongCountAsync(ct).ConfigureAwait(false);
	}

	public async ValueTask<long> DeleteOlderThanAsync(
		WorkspaceId workspaceId,
		DateTimeOffset cutoff,
		CancellationToken ct)
	{
		var cutoffMs = cutoff.ToUnixTimeMilliseconds();
		await using var db = Open(workspaceId);
		return await db.GetTable<EventRecord>()
			.Where(e => e.TimestampMs < cutoffMs)
			.DeleteAsync(ct)
			.ConfigureAwait(false);
	}

	public async ValueTask<long> DeleteKqlAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var source = db.GetTable<EventRecord>();
		var filtered = KqlTransformer.Apply(source.AsQueryable(), kql);
		return await filtered.DeleteAsync(ct).ConfigureAwait(false);
	}

	public async ValueTask DeclareIndexAsync(
		WorkspaceId workspaceId,
		string propertyPath,
		IndexKind kind,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(propertyPath))
			throw new ArgumentException("property path required", nameof(propertyPath));

		await using var db = Open(workspaceId);
		var safeName = SanitizeIndexName(propertyPath);
		var sql = $"CREATE INDEX IF NOT EXISTS ix_prop_{safeName} ON Events(json_extract(PropertiesJson, '$.{propertyPath}'));";
		await db.ExecuteAsync(sql, ct).ConfigureAwait(false);
	}

	public async ValueTask CompactAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		await db.ExecuteAsync("VACUUM;", ct).ConfigureAwait(false);
	}

	public async ValueTask<WorkspaceStats> GetStatsAsync(WorkspaceId workspaceId, CancellationToken ct)
	{
		await using var db = Open(workspaceId);
		var table = db.GetTable<EventRecord>();
		var count = await table.LongCountAsync(ct).ConfigureAwait(false);
		var oldestMs = count == 0
			? (long?)null
			: await table.MinAsync(e => e.TimestampMs, ct).ConfigureAwait(false);

		var path = PathFor(workspaceId);
		var size = File.Exists(path) ? new FileInfo(path).Length : 0;

		return new WorkspaceStats(
			count,
			size,
			oldestMs is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(oldestMs.Value));
	}

	static IQueryable<EventRecord> BuildQuery(DataConnection db, LogQuery query)
	{
		var q = db.GetTable<EventRecord>().AsQueryable();

		if (query.From is { } from)
		{
			var fromMs = from.ToUnixTimeMilliseconds();
			q = q.Where(e => e.TimestampMs >= fromMs);
		}

		if (query.To is { } to)
		{
			var toMs = to.ToUnixTimeMilliseconds();
			q = q.Where(e => e.TimestampMs < toMs);
		}

		if (query.MinLevel is { } lvl)
		{
			var minLevel = (int)lvl;
			q = q.Where(e => e.Level >= minLevel);
		}

		if (query.TraceId is { } tr)
			q = q.Where(e => e.TraceId == tr);

		if (!string.IsNullOrEmpty(query.MessageSubstring))
		{
			var sub = query.MessageSubstring;
			q = q.Where(e => e.Message.Contains(sub));
		}

		if (query.Cursor is { } cursor)
		{
			var (ts, id) = CursorCodec.Decode(cursor.Span);
			q = q.Where(e => e.TimestampMs < ts || (e.TimestampMs == ts && e.Id < id));
		}

		return q;
	}

	static LogEvent ToLogEvent(EventRecord r) => new(
		r.Id,
		DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs),
		(LogLevel)r.Level,
		r.MessageTemplate,
		r.Message,
		r.Exception,
		r.TraceId,
		r.SpanId,
		r.EventId,
		DeserializeProperties(r.PropertiesJson));

	static ImmutableDictionary<string, JsonElement> DeserializeProperties(string json)
	{
		if (string.IsNullOrEmpty(json) || json == "{}")
			return ImmutableDictionary<string, JsonElement>.Empty;

		using var doc = JsonDocument.Parse(json);
		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var prop in doc.RootElement.EnumerateObject())
			builder[prop.Name] = prop.Value.Clone();
		return builder.ToImmutable();
	}

	static string SanitizeIndexName(string path)
	{
		var buf = new char[path.Length];
		for (var i = 0; i < path.Length; i++)
		{
			var c = path[i];
			buf[i] = char.IsLetterOrDigit(c) ? c : '_';
		}
		return new string(buf);
	}
}
