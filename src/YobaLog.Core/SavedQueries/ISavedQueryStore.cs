namespace YobaLog.Core.SavedQueries;

public interface ISavedQueryStore
{
	ValueTask InitializeWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask<SavedQuery> UpsertAsync(WorkspaceId workspaceId, string name, string kql, CancellationToken ct);

	ValueTask<SavedQuery?> GetAsync(WorkspaceId workspaceId, long id, CancellationToken ct);

	ValueTask<SavedQuery?> GetByNameAsync(WorkspaceId workspaceId, string name, CancellationToken ct);

	ValueTask<IReadOnlyList<SavedQuery>> ListAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask<bool> DeleteAsync(WorkspaceId workspaceId, long id, CancellationToken ct);

	ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
}
