using System.Text;
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
	string PropertiesJson,
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
		SerializeProperties(e),
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
		PropertiesJson: "",
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

	static string SerializeProperties(LogEvent e)
	{
		if (e.Properties.Count == 0)
			return "";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			foreach (var (k, v) in e.Properties)
			{
				writer.WritePropertyName(k);
				v.WriteTo(writer);
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
