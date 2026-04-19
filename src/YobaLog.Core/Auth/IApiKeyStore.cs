namespace YobaLog.Core.Auth;

public interface IApiKeyStore
{
	ValueTask<ApiKeyValidation> ValidateAsync(string? token, CancellationToken ct);

	IReadOnlyCollection<WorkspaceId> ConfiguredWorkspaces { get; }
}
