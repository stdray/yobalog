namespace YobaLog.Core.Retention;

// DB-backed catalog of retention rules. Stored in $system.meta.db alongside Workspaces and Users.
// Each rule binds a workspace-scoped saved-query name to a retention horizon in days —
// deletion logic matches events against the saved query and drops those older than the cutoff.
//
// Config (`Retention:Policies[]` in appsettings) stays as a bootstrap/recovery seed only:
// RetentionService's consumer reads DB when non-empty, config when DB is empty.
public interface IRetentionPolicyStore
{
	ValueTask InitializeAsync(CancellationToken ct);

	ValueTask<IReadOnlyList<RetentionPolicy>> ListAsync(CancellationToken ct);

	ValueTask<IReadOnlyList<RetentionPolicy>> ListByWorkspaceAsync(WorkspaceId workspace, CancellationToken ct);

	ValueTask UpsertAsync(RetentionPolicy policy, CancellationToken ct);

	// False if no row matched (Workspace, SavedQuery).
	ValueTask<bool> DeleteAsync(WorkspaceId workspace, string savedQuery, CancellationToken ct);
}
