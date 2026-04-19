namespace YobaLog.Core.Storage;

public sealed record WorkspaceStats(
	long EventCount,
	long SizeBytes,
	DateTimeOffset? OldestEvent);
