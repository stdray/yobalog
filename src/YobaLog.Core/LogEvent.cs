using System.Collections.Immutable;
using System.Text.Json;

namespace YobaLog.Core;

public sealed record LogEvent(
	long Id,
	DateTimeOffset Timestamp,
	LogLevel Level,
	string MessageTemplate,
	string Message,
	string? Exception,
	string? TraceId,
	string? SpanId,
	int? EventId,
	ImmutableDictionary<string, JsonElement> Properties);
