using Microsoft.Extensions.Hosting;
using YobaLog.Core.Storage;

namespace YobaLog.Core.SelfLogging;

public sealed class SystemLogFlusher : BackgroundService
{
	readonly ILogStore _store;
	readonly SystemLoggerProvider _provider;

	public SystemLogFlusher(ILogStore store, SystemLoggerProvider provider)
	{
		_store = store;
		_provider = provider;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var batchSize = _provider.Options.BatchSize;
		var batch = new List<LogEventCandidate>(batchSize);

		while (!stoppingToken.IsCancellationRequested)
		{
			batch.Clear();
			try
			{
				if (!await _provider.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
					return;

				while (batch.Count < batchSize && _provider.Reader.TryRead(out var ev))
					batch.Add(ev);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			if (batch.Count == 0)
				continue;

			try
			{
				await _store.AppendBatchAsync(WorkspaceId.System, batch, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// Bypass ILogger — recursion would be ours. Console.Error is the safe sink.
				await Console.Error.WriteLineAsync($"YobaLog self-log flush failed: {ex.Message}").ConfigureAwait(false);
			}
		}
	}
}
