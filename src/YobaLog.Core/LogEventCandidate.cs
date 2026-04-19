using System.Collections.Immutable;
using System.Text.Json;

namespace YobaLog.Core;

public sealed record LogEventCandidate(
	DateTimeOffset Timestamp,
	LogLevel Level,
	string MessageTemplate,
	string Message,
	string? Exception,
	string? TraceId,
	string? SpanId,
	int? EventId,
	ImmutableDictionary<string, JsonElement> Properties);
