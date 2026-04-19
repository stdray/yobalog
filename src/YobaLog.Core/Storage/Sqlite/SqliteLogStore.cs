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
	readonly KqlTransformer _kql = new();
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
		var translated = _kql.Apply(source, kql);

		await foreach (var r in translated.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return ToLogEvent(r);
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
			.BulkCopyAsync(batch.Select(ToRecord), ct)
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

	static EventRecord ToRecord(LogEventCandidate c) => new()
	{
		TimestampMs = c.Timestamp.ToUnixTimeMilliseconds(),
		Level = (int)c.Level,
		MessageTemplate = c.MessageTemplate,
		Message = c.Message,
		Exception = c.Exception,
		TraceId = c.TraceId,
		SpanId = c.SpanId,
		EventId = c.EventId,
		TemplateHash = StableHash(c.MessageTemplate),
		PropertiesJson = SerializeProperties(c.Properties),
	};

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

	static string SerializeProperties(ImmutableDictionary<string, JsonElement> props)
	{
		if (props.IsEmpty)
			return "{}";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			foreach (var (k, v) in props)
			{
				writer.WritePropertyName(k);
				v.WriteTo(writer);
			}
			writer.WriteEndObject();
		}
		return System.Text.Encoding.UTF8.GetString(stream.ToArray());
	}

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

	static long StableHash(string s)
	{
		// FNV-1a 64-bit — stable across runs (unlike string.GetHashCode).
		const long offset = unchecked((long)14695981039346656037);
		const long prime = 1099511628211;
		var h = offset;
		foreach (var c in s)
		{
			h ^= c;
			h *= prime;
		}
		return h;
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
