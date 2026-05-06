namespace YobaLog.Core.Sharing;

public sealed record KqlShareLink(
    string Id,
    WorkspaceId Workspace,
    string Kql,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
