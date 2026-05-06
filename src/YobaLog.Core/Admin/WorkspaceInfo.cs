namespace YobaLog.Core.Admin;

public sealed record WorkspaceInfo(WorkspaceId Id, DateTimeOffset CreatedAt, string Description = "", string Agent = "", string GroupName = "");
