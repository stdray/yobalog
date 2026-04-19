using System.Collections.Immutable;

namespace YobaLog.Core.Sharing;

public interface IShareLinkStore
{
	ValueTask InitializeWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask<ShareLink> CreateAsync(
		WorkspaceId workspaceId,
		string kql,
		DateTimeOffset expiresAt,
		ImmutableArray<string> columns,
		ImmutableDictionary<string, MaskMode> modes,
		CancellationToken ct);

	ValueTask<ShareLink?> GetAsync(WorkspaceId workspaceId, string id, CancellationToken ct);

	ValueTask<bool> DeleteAsync(WorkspaceId workspaceId, string id, CancellationToken ct);

	ValueTask<int> DeleteExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, CancellationToken ct);

	ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
}
