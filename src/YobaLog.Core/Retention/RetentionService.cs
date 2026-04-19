using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.Storage;

namespace YobaLog.Core.Retention;

public sealed class RetentionService : BackgroundService
{
	readonly ILogStore _store;
	readonly IApiKeyStore _apiKeys;
	readonly RetentionOptions _options;
	readonly ILogger<RetentionService> _logger;
	readonly TimeProvider _time;

	public RetentionService(
		ILogStore store,
		IApiKeyStore apiKeys,
		IOptions<RetentionOptions> options,
		ILogger<RetentionService> logger,
		TimeProvider? time = null)
	{
		_store = store;
		_apiKeys = apiKeys;
		_options = options.Value;
		_logger = logger;
		_time = time ?? TimeProvider.System;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunPassAsync(_time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				RetentionLog.PassFailed(_logger, ex);
			}

			try
			{
				await Task.Delay(_options.RunInterval, _time, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	public async Task RunPassAsync(DateTimeOffset now, CancellationToken ct)
	{
		var userCutoff = now - TimeSpan.FromDays(_options.RetentionDays);
		var systemCutoff = now - TimeSpan.FromDays(_options.SystemRetentionDays);

		foreach (var ws in _apiKeys.ConfiguredWorkspaces)
			await SweepAsync(ws, userCutoff, ct).ConfigureAwait(false);

		await SweepAsync(WorkspaceId.System, systemCutoff, ct).ConfigureAwait(false);
	}

	async Task SweepAsync(WorkspaceId ws, DateTimeOffset cutoff, CancellationToken ct)
	{
		try
		{
			var deleted = await _store.DeleteOlderThanAsync(ws, cutoff, ct).ConfigureAwait(false);
			RetentionLog.Swept(_logger, ws, deleted, cutoff);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			RetentionLog.WorkspaceFailed(_logger, ex, ws);
		}
	}
}

static partial class RetentionLog
{
	[LoggerMessage(EventId = 20, Level = Microsoft.Extensions.Logging.LogLevel.Information,
		Message = "Retention swept {Workspace}: deleted {Count} events older than {Cutoff:O}")]
	public static partial void Swept(ILogger logger, WorkspaceId workspace, long count, DateTimeOffset cutoff);

	[LoggerMessage(EventId = 21, Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Retention failed on workspace {Workspace}")]
	public static partial void WorkspaceFailed(ILogger logger, Exception ex, WorkspaceId workspace);

	[LoggerMessage(EventId = 22, Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Retention pass failed")]
	public static partial void PassFailed(ILogger logger, Exception ex);
}
