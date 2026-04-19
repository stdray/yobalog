using YobaLog.Core.Admin;

namespace YobaLog.Core.Auth;

// Admin CRUD for per-workspace API keys stored in `<ws>.meta.db`.
// Hot-path validation goes through IApiKeyStore (which may compose over multiple backends —
// e.g. config keys as an admin backdoor + DB-backed keys managed via this interface).
public interface IApiKeyAdmin
{
	ValueTask InitializeWorkspaceAsync(WorkspaceId workspace, CancellationToken ct);

	ValueTask<IReadOnlyList<ApiKeyInfo>> ListAsync(WorkspaceId workspace, CancellationToken ct);

	// Returns plaintext exactly once — caller must surface it immediately and never persist.
	ValueTask<ApiKeyCreated> CreateAsync(WorkspaceId workspace, string? title, CancellationToken ct);

	ValueTask<bool> DeleteAsync(WorkspaceId workspace, string id, CancellationToken ct);

	// Called by the workspace lifecycle when a workspace is dropped — purges the in-memory
	// validation cache and deletes the underlying `<ws>.meta.db` file alongside its wal/shm
	// siblings. Schema rows disappear with the file.
	ValueTask DropWorkspaceAsync(WorkspaceId workspace, CancellationToken ct);
}
