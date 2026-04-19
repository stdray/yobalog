using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaLog.Core.Auth;

namespace YobaLog.Core.Storage;

public sealed class WorkspaceBootstrapper : IHostedService
{
	readonly ILogStore _store;
	readonly IApiKeyStore _apiKeys;
	readonly ILogger<WorkspaceBootstrapper> _logger;

	public WorkspaceBootstrapper(ILogStore store, IApiKeyStore apiKeys, ILogger<WorkspaceBootstrapper> logger)
	{
		_store = store;
		_apiKeys = apiKeys;
		_logger = logger;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), cancellationToken)
			.ConfigureAwait(false);
		BootstrapLog.SystemCreated(_logger);

		foreach (var ws in _apiKeys.ConfiguredWorkspaces)
		{
			await _store.CreateWorkspaceAsync(ws, new WorkspaceSchema(), cancellationToken)
				.ConfigureAwait(false);
			BootstrapLog.WorkspaceCreated(_logger, ws);
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

static partial class BootstrapLog
{
	[LoggerMessage(EventId = 10, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "System workspace ready")]
	public static partial void SystemCreated(ILogger logger);

	[LoggerMessage(EventId = 11, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Workspace ready: {Workspace}")]
	public static partial void WorkspaceCreated(ILogger logger, WorkspaceId workspace);
}
