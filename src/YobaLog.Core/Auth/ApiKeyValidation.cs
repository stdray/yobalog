namespace YobaLog.Core.Auth;

public sealed record ApiKeyValidation(bool IsValid, WorkspaceId? Scope, string? Reason)
{
	public static ApiKeyValidation Valid(WorkspaceId scope) => new(true, scope, null);
	public static ApiKeyValidation Invalid(string reason) => new(false, null, reason);
}
