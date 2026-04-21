using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaLog.Core.Admin;
using YobaLog.Core.Auth;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;

namespace YobaLog.Core.Storage;

public sealed class WorkspaceBootstrapper : IHostedService
{
	readonly ILogStore _store;
	readonly ISavedQueryStore _savedQueries;
	readonly IFieldMaskingPolicyStore _maskingPolicies;
	readonly IShareLinkStore _shareLinks;
	readonly IWorkspaceStore _workspaceStore;
	readonly IUserStore _userStore;
	readonly IRetentionPolicyStore _retentionPolicies;
	readonly IApiKeyStore _apiKeys;
	readonly IApiKeyAdmin _apiKeyAdmin;
	readonly ILogger<WorkspaceBootstrapper> _logger;

	public WorkspaceBootstrapper(
		ILogStore store,
		ISavedQueryStore savedQueries,
		IFieldMaskingPolicyStore maskingPolicies,
		IShareLinkStore shareLinks,
		IWorkspaceStore workspaceStore,
		IUserStore userStore,
		IRetentionPolicyStore retentionPolicies,
		IApiKeyStore apiKeys,
		IApiKeyAdmin apiKeyAdmin,
		ILogger<WorkspaceBootstrapper> logger)
	{
		_store = store;
		_savedQueries = savedQueries;
		_maskingPolicies = maskingPolicies;
		_shareLinks = shareLinks;
		_workspaceStore = workspaceStore;
		_userStore = userStore;
		_retentionPolicies = retentionPolicies;
		_apiKeys = apiKeys;
		_apiKeyAdmin = apiKeyAdmin;
		_logger = logger;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await _workspaceStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
		await _userStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
		await _retentionPolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);

		await InitMetaAsync(WorkspaceId.System, cancellationToken).ConfigureAwait(false);
		await _store.CreateWorkspaceAsync(WorkspaceId.System, new WorkspaceSchema(), cancellationToken).ConfigureAwait(false);
		BootstrapLog.SystemCreated(_logger);

		// First-run migration: seed workspace store from config's API keys if the store is empty.
		// Subsequent runs read exclusively from the store; config becomes a bootstrap seed only.
		var known = await _workspaceStore.ListAsync(cancellationToken).ConfigureAwait(false);
		if (known.Count == 0 && _apiKeys.ConfiguredWorkspaces.Count > 0)
		{
			foreach (var ws in _apiKeys.ConfiguredWorkspaces)
				await _workspaceStore.CreateAsync(ws, cancellationToken).ConfigureAwait(false);
			known = await _workspaceStore.ListAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach (var info in known)
		{
			await _store.CreateWorkspaceAsync(info.Id, new WorkspaceSchema(), cancellationToken).ConfigureAwait(false);
			await InitMetaAsync(info.Id, cancellationToken).ConfigureAwait(false);
			BootstrapLog.WorkspaceCreated(_logger, info.Id);
		}
	}

	async Task InitMetaAsync(WorkspaceId ws, CancellationToken ct)
	{
		await _savedQueries.InitializeWorkspaceAsync(ws, ct).ConfigureAwait(false);
		await _maskingPolicies.InitializeWorkspaceAsync(ws, ct).ConfigureAwait(false);
		await _shareLinks.InitializeWorkspaceAsync(ws, ct).ConfigureAwait(false);
		await _apiKeyAdmin.InitializeWorkspaceAsync(ws, ct).ConfigureAwait(false);
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
