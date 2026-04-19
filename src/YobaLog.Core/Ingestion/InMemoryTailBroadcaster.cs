using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kusto.Language;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Core.Ingestion;

public sealed class InMemoryTailBroadcaster : ITailBroadcaster
{
	readonly ConcurrentDictionary<WorkspaceId, ImmutableList<ChannelWriter<LogEventCandidate>>> _subscribers = new();
	readonly KqlTransformer _transformer = new();

	public int WindowSize { get; init; } = 100;

	public async IAsyncEnumerable<LogEventCandidate> Subscribe(
		WorkspaceId workspaceId,
		KustoCode query,
		[EnumeratorCancellation] CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(query);
		var predicate = CompilePredicate(query);

		var channel = Channel.CreateBounded<LogEventCandidate>(new BoundedChannelOptions(WindowSize)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
		});

		_subscribers.AddOrUpdate(
			workspaceId,
			_ => [channel.Writer],
			(_, list) => list.Add(channel.Writer));

		try
		{
			await foreach (var candidate in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
			{
				if (predicate(candidate))
					yield return candidate;
			}
		}
		finally
		{
			_subscribers.AddOrUpdate(
				workspaceId,
				_ => [],
				(_, list) => list.Remove(channel.Writer));
			channel.Writer.TryComplete();
		}
	}

	public void Publish(WorkspaceId workspaceId, IReadOnlyList<LogEventCandidate> batch)
	{
		if (batch.Count == 0)
			return;
		if (!_subscribers.TryGetValue(workspaceId, out var writers) || writers.Count == 0)
			return;

		foreach (var writer in writers)
		{
			foreach (var candidate in batch)
				writer.TryWrite(candidate);
		}
	}

	Func<LogEventCandidate, bool> CompilePredicate(KustoCode query)
	{
		// Eagerly validate the query — failure surfaces at subscribe time, not per-event.
		_ = _transformer.Apply(Array.Empty<EventRecord>().AsQueryable(), query).ToList();

		return candidate =>
		{
			var record = ToRecord(candidate);
			return _transformer.Apply(new[] { record }.AsQueryable(), query).Any();
		};
	}

	static EventRecord ToRecord(LogEventCandidate c) => new()
	{
		Id = 0,
		TimestampMs = c.Timestamp.ToUnixTimeMilliseconds(),
		Level = (int)c.Level,
		MessageTemplate = c.MessageTemplate,
		Message = c.Message,
		Exception = c.Exception,
		TraceId = c.TraceId,
		SpanId = c.SpanId,
		EventId = c.EventId,
		PropertiesJson = "{}",
	};
}
