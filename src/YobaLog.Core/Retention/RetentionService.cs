using System.Globalization;
using Kusto.Language;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;

namespace YobaLog.Core.Retention;

public sealed class RetentionService : BackgroundService
{
	readonly ILogStore _store;
	readonly ISavedQueryStore _savedQueries;
	readonly IShareLinkStore _shareLinks;
	readonly IApiKeyStore _apiKeys;
	readonly RetentionOptions _options;
	readonly ILogger<RetentionService> _logger;
	readonly TimeProvider _time;

	public RetentionService(
		ILogStore store,
		ISavedQueryStore savedQueries,
		IShareLinkStore shareLinks,
		IApiKeyStore apiKeys,
		IOptions<RetentionOptions> options,
		ILogger<RetentionService> logger,
		TimeProvider? time = null)
	{
		_store = store;
		_savedQueries = savedQueries;
		_shareLinks = shareLinks;
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
			catch (OperationCanceledException) { break; }
			catch (Exception ex) { RetentionLog.PassFailed(_logger, ex); }

			try
			{
				await Task.Delay(_options.RunInterval, _time, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
		}
	}

	public async Task RunPassAsync(DateTimeOffset now, CancellationToken ct)
	{
		foreach (var ws in _apiKeys.ConfiguredWorkspaces)
		{
			await SweepWorkspaceAsync(ws, now, ct).ConfigureAwait(false);
			await SweepShareLinksAsync(ws, now, ct).ConfigureAwait(false);
		}

		await SweepSystemAsync(now, ct).ConfigureAwait(false);
		await SweepShareLinksAsync(WorkspaceId.System, now, ct).ConfigureAwait(false);
	}

	async Task SweepShareLinksAsync(WorkspaceId ws, DateTimeOffset now, CancellationToken ct)
	{
		try
		{
			var deleted = await _shareLinks.DeleteExpiredAsync(ws, now, ct).ConfigureAwait(false);
			if (deleted > 0)
				RetentionLog.SweptShareLinks(_logger, ws, deleted);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			RetentionLog.ShareLinksFailed(_logger, ex, ws);
		}
	}

	async Task SweepWorkspaceAsync(WorkspaceId ws, DateTimeOffset now, CancellationToken ct)
	{
		var policies = _options.Policies
			.Where(p => string.Equals(p.Workspace, ws.Value, StringComparison.Ordinal))
			.ToList();

		if (policies.Count == 0)
		{
			var cutoff = now - TimeSpan.FromDays(_options.DefaultRetainDays);
			await SweepByTimeAsync(ws, cutoff, ct).ConfigureAwait(false);
			return;
		}

		foreach (var policy in policies)
			await SweepByPolicyAsync(ws, policy, now, ct).ConfigureAwait(false);
	}

	async Task SweepSystemAsync(DateTimeOffset now, CancellationToken ct)
	{
		var cutoff = now - TimeSpan.FromDays(_options.SystemRetainDays);
		await SweepByTimeAsync(WorkspaceId.System, cutoff, ct).ConfigureAwait(false);
	}

	async Task SweepByTimeAsync(WorkspaceId ws, DateTimeOffset cutoff, CancellationToken ct)
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

	async Task SweepByPolicyAsync(WorkspaceId ws, RetentionPolicy policy, DateTimeOffset now, CancellationToken ct)
	{
		var saved = await _savedQueries.GetByNameAsync(ws, policy.SavedQuery, ct).ConfigureAwait(false);
		if (saved is null)
		{
			RetentionLog.MissingSavedQuery(_logger, ws, policy.SavedQuery);
			return;
		}

		var cutoff = now - TimeSpan.FromDays(policy.RetainDays);
		var cutoffLiteral = $"datetime({cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)})";
		var kql = $"{saved.Kql.TrimEnd()}\n| where Timestamp < {cutoffLiteral}";

		try
		{
			var code = KustoCode.Parse(kql);
			var deleted = await _store.DeleteKqlAsync(ws, code, ct).ConfigureAwait(false);
			RetentionLog.SweptByPolicy(_logger, ws, policy.SavedQuery, deleted, cutoff);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			RetentionLog.PolicyFailed(_logger, ex, ws, policy.SavedQuery);
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

	[LoggerMessage(EventId = 23, Level = Microsoft.Extensions.Logging.LogLevel.Information,
		Message = "Retention policy {Policy} on {Workspace} deleted {Count} events older than {Cutoff:O}")]
	public static partial void SweptByPolicy(ILogger logger, WorkspaceId workspace, string policy, long count, DateTimeOffset cutoff);

	[LoggerMessage(EventId = 24, Level = Microsoft.Extensions.Logging.LogLevel.Warning,
		Message = "Retention policy references missing saved query {Policy} in {Workspace}")]
	public static partial void MissingSavedQuery(ILogger logger, WorkspaceId workspace, string policy);

	[LoggerMessage(EventId = 25, Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Retention policy {Policy} on {Workspace} failed")]
	public static partial void PolicyFailed(ILogger logger, Exception ex, WorkspaceId workspace, string policy);

	[LoggerMessage(EventId = 26, Level = Microsoft.Extensions.Logging.LogLevel.Information,
		Message = "Share-link sweep on {Workspace}: deleted {Count} expired")]
	public static partial void SweptShareLinks(ILogger logger, WorkspaceId workspace, long count);

	[LoggerMessage(EventId = 27, Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Share-link sweep on {Workspace} failed")]
	public static partial void ShareLinksFailed(ILogger logger, Exception ex, WorkspaceId workspace);
}
