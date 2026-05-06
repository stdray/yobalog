namespace YobaLog.Core.Auth;

public sealed record ApiKeyValidation(
    bool IsValid,
    bool IsWildcard,
    bool CanCreate,
    DateTimeOffset? CreateDeadline,
    WorkspaceId? Scope,
    string? Reason,
    string? Title = null)
{
    public static ApiKeyValidation Valid(WorkspaceId scope, string? title = null) =>
        new(true, false, false, null, scope, null, title);

    public static ApiKeyValidation Wildcard(bool canCreate, DateTimeOffset? createDeadline, string? title = null) =>
        new(true, true, canCreate, createDeadline, null, null, title);

    public static ApiKeyValidation Invalid(string reason) =>
        new(false, false, false, null, null, reason);
}
