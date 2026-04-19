namespace YobaLog.Core.Storage;

public sealed record LogQuery(
	int PageSize,
	DateTimeOffset? From = null,
	DateTimeOffset? To = null,
	LogLevel? MinLevel = null,
	string? CategoryPrefix = null,
	string? TraceId = null,
	string? MessageSubstring = null,
	ReadOnlyMemory<byte>? Cursor = null);
