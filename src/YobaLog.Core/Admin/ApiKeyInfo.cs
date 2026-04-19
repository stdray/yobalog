namespace YobaLog.Core.Admin;

public sealed record ApiKeyInfo(
	string Id,
	string Prefix,
	WorkspaceId Workspace,
	string? Title,
	DateTimeOffset CreatedAt);
