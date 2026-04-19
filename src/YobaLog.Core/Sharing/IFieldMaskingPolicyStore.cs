namespace YobaLog.Core.Sharing;

public interface IFieldMaskingPolicyStore
{
	ValueTask InitializeWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask<FieldMaskingPolicy> GetAsync(WorkspaceId workspaceId, CancellationToken ct);

	ValueTask UpsertAsync(WorkspaceId workspaceId, IReadOnlyDictionary<string, MaskMode> modes, CancellationToken ct);

	ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
}
