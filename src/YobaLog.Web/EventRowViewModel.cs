using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using YobaLog.Core;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web;

public sealed record EventRowViewModel(
	long? Id,
	DateTimeOffset Timestamp,
	LogLevel Level,
	string MessageTemplate,
	string Message,
	string? Exception,
	string? TraceId,
	string? SpanId,
	int? EventId,
	ImmutableDictionary<string, JsonElement> Properties,
	bool IsLive)
{
	public static EventRowViewModel FromStored(LogEvent e) => new(
		e.Id,
		e.Timestamp,
		e.Level,
		e.MessageTemplate,
		e.Message,
		e.Exception,
		e.TraceId,
		e.SpanId,
		e.EventId,
		e.Properties,
		IsLive: false);

	public static EventRowViewModel FromLive(LogEventCandidate c) => new(
		Id: null,
		c.Timestamp,
		c.Level,
		c.MessageTemplate,
		c.Message,
		c.Exception,
		c.TraceId,
		c.SpanId,
		c.EventId,
		c.Properties,
		IsLive: true);

	public static string LevelBadge(LogLevel l) => l switch
	{
		LogLevel.Verbose => "badge-ghost",
		LogLevel.Debug => "badge-ghost",
		LogLevel.Information => "badge-info",
		LogLevel.Warning => "badge-warning",
		LogLevel.Error => "badge-error",
		LogLevel.Fatal => "badge-error",
		_ => "badge-ghost",
	};

	public static string KqlString(string? s) =>
		"'" + (s ?? "")
			.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("'", "\\'", StringComparison.Ordinal) + "'";

	public static string KqlDatetime(DateTimeOffset dt) =>
		"datetime(" + IsoUtc(dt) + ")";

	public static string IsoUtc(DateTimeOffset dt) =>
		dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

	/// <summary>Returns display text + KQL literal for a property value.
	/// KQL literal is null if the value isn't filterable as a string (e.g. nested object/array).</summary>
	public static (string Display, string? KqlLiteral) PropertyForDisplay(JsonElement v) => v.ValueKind switch
	{
		JsonValueKind.String => (v.GetString() ?? "", KqlString(v.GetString())),
		JsonValueKind.Null or JsonValueKind.Undefined => ("(null)", null),
		JsonValueKind.Number => (v.GetRawText(), v.GetRawText()),
		JsonValueKind.True => ("true", "true"),
		JsonValueKind.False => ("false", "false"),
		_ => (v.GetRawText(), null),
	};
}
