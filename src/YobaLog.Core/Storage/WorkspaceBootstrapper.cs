using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaLog.Core.Auth;
using YobaLog.Core.SavedQueries;

namespace YobaLog.Core.Storage;

public sealed class WorkspaceBootstrapper : IHostedService
{
	readonly ILogStore _store;
	readonly ISavedQueryStore _savedQueries;
	readonly IApiKeyStore _apiKeys;
	readonly ILogger<WorkspaceBootstrapper> _logger;

	public WorkspaceBootstrapper(
		ILogStore store,
		ISavedQueryStore savedQueries,
		IApiKeyStore apiKeys,
		ILogger<WorkspaceBootstrapper> logger)
	{
		_store = store;
		_savedQueries = savedQueries;
		_apiKeys = apiKeys;
		_logger = logger;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await BootstrapAsync(WorkspaceId.System, cancellationToken).ConfigureAwait(false);
		BootstrapLog.SystemCreated(_logger);

		foreach (var ws in _apiKeys.ConfiguredWorkspaces)
		{
			await BootstrapAsync(ws, cancellationToken).ConfigureAwait(false);
			BootstrapLog.WorkspaceCreated(_logger, ws);
		}
	}

	async Task BootstrapAsync(WorkspaceId ws, CancellationToken ct)
	{
		await _store.CreateWorkspaceAsync(ws, new WorkspaceSchema(), ct).ConfigureAwait(false);
		await _savedQueries.InitializeWorkspaceAsync(ws, ct).ConfigureAwait(false);
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
