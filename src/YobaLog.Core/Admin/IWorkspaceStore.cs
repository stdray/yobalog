namespace YobaLog.Core.Admin;

public interface IWorkspaceStore
{
	ValueTask InitializeAsync(CancellationToken ct);

	ValueTask<IReadOnlyList<WorkspaceInfo>> ListAsync(CancellationToken ct);

	ValueTask<WorkspaceInfo?> GetAsync(WorkspaceId id, CancellationToken ct);

	ValueTask<WorkspaceInfo> CreateAsync(WorkspaceId id, CancellationToken ct);

	ValueTask<bool> DeleteAsync(WorkspaceId id, CancellationToken ct);
}
