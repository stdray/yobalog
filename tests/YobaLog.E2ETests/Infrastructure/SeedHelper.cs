using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Auth;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;

namespace YobaLog.E2ETests.Infrastructure;

public static class SeedHelper
{
	static readonly ImmutableDictionary<string, JsonElement> EmptyProps =
		ImmutableDictionary<string, JsonElement>.Empty;

	public static LogEventCandidate Event(LogLevel level, string message, DateTimeOffset? at = null) =>
		new(
			Timestamp: at ?? DateTimeOffset.UtcNow,
			Level: level,
			MessageTemplate: message,
			Message: message,
			Exception: null,
			TraceId: null,
			SpanId: null,
			EventId: null,
			Properties: EmptyProps);

	public static async Task SeedAsync(this WebAppFixture fixture, string workspace, params LogEventCandidate[] events)
	{
		var ws = WorkspaceId.Parse(workspace);
		// Idempotently init both logs.db (for events) and meta.db (for saved queries / share links /
		// masking / api keys) — mirrors what WorkspaceBootstrapper does at startup. Fresh per-test
		// workspaces go straight to SeedAsync so this is the only init path they get.
		await fixture.LogStore.CreateWorkspaceAsync(ws, new WorkspaceSchema(), CancellationToken.None);
		var sp = fixture.Services;
		await sp.GetRequiredService<ISavedQueryStore>().InitializeWorkspaceAsync(ws, CancellationToken.None);
		await sp.GetRequiredService<IFieldMaskingPolicyStore>().InitializeWorkspaceAsync(ws, CancellationToken.None);
		await sp.GetRequiredService<IShareLinkStore>().InitializeWorkspaceAsync(ws, CancellationToken.None);
		await sp.GetRequiredService<IApiKeyAdmin>().InitializeWorkspaceAsync(ws, CancellationToken.None);
		await fixture.LogStore.AppendBatchAsync(ws, events, CancellationToken.None);
	}

	// A slug unique to the caller test. Prevents seed bleed when multiple tests share the same
	// UiCollection fixture. Format: `<prefix>-<guid>`.
	public static string FreshWorkspace(string prefix) =>
		$"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}
