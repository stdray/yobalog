using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaLog.Core.Observability;
using YobaLog.Core.Storage;

namespace YobaLog.Core.Ingestion;

public sealed class ChannelIngestionPipeline : IIngestionPipeline, IHostedService, IAsyncDisposable
{
	readonly ILogStore _store;
	readonly ITailBroadcaster? _tail;
	readonly IngestionOptions _options;
	readonly ILogger<ChannelIngestionPipeline> _logger;
	readonly ConcurrentDictionary<WorkspaceId, WorkspaceChannel> _channels = new();
	int _disposed;

	public ChannelIngestionPipeline(
		ILogStore store,
		IOptions<IngestionOptions> options,
		ILogger<ChannelIngestionPipeline> logger,
		ITailBroadcaster? tail = null)
	{
		_store = store;
		_tail = tail;
		_options = options.Value;
		_logger = logger;
	}

	public async ValueTask IngestAsync(
		WorkspaceId workspaceId,
		IReadOnlyList<LogEventCandidate> batch,
		CancellationToken ct)
	{
		if (batch.Count == 0)
			return;

		using var activity = workspaceId.IsSystem ? null : Tracing.Ingestion.StartActivity("ingest.enqueue");
		activity?.SetTag("workspace", workspaceId.Value);
		activity?.SetTag("batch.size", batch.Count);

		var wc = _channels.GetOrAdd(workspaceId, StartChannel);
		foreach (var candidate in batch)
			await wc.Writer.WriteAsync(candidate, ct).ConfigureAwait(false);
	}

	WorkspaceChannel StartChannel(WorkspaceId ws)
	{
		var channel = Channel.CreateBounded<LogEventCandidate>(new BoundedChannelOptions(_options.ChannelCapacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false,
		});
		var loop = Task.Run(() => WriterLoopAsync(ws, channel.Reader));
		return new WorkspaceChannel(channel.Writer, loop);
	}

	async Task WriterLoopAsync(WorkspaceId ws, ChannelReader<LogEventCandidate> reader)
	{
		var batch = new List<LogEventCandidate>(_options.MaxBatchSize);
		while (await reader.WaitToReadAsync().ConfigureAwait(false))
		{
			batch.Clear();
			while (batch.Count < _options.MaxBatchSize && reader.TryRead(out var candidate))
				batch.Add(candidate);

			if (batch.Count == 0)
				continue;

			try
			{
				await _store.AppendBatchAsync(ws, batch, CancellationToken.None).ConfigureAwait(false);
				_tail?.Publish(ws, batch);
			}
			catch (Exception ex)
			{
				IngestionLog.AppendBatchFailed(_logger, ex, batch.Count, ws);
			}
		}
	}

	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		foreach (var wc in _channels.Values)
			wc.Writer.TryComplete();

		try
		{
			await Task.WhenAll(_channels.Values.Select(w => w.Loop))
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			IngestionLog.ShutdownTimedOut(_logger);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		foreach (var wc in _channels.Values)
			wc.Writer.TryComplete();

		await Task.WhenAll(_channels.Values.Select(w => w.Loop)).ConfigureAwait(false);
	}

	sealed record WorkspaceChannel(ChannelWriter<LogEventCandidate> Writer, Task Loop);
}
