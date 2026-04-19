using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using LinqToDB.Mapping;

namespace YobaLog.Core.Storage.Sqlite;

[Table("Events")]
sealed class EventRecord
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public long TimestampMs { get; set; }
	[Column, NotNull] public int Level { get; set; }
	[Column, NotNull] public string MessageTemplate { get; set; } = "";
	[Column, NotNull] public string Message { get; set; } = "";
	[Column, Nullable] public string? Exception { get; set; }
	[Column, Nullable] public string? TraceId { get; set; }
	[Column, Nullable] public string? SpanId { get; set; }
	[Column, Nullable] public int? EventId { get; set; }
	[Column, NotNull] public long TemplateHash { get; set; }
	[Column, NotNull] public string PropertiesJson { get; set; } = "{}";

	public static EventRecord FromCandidate(LogEventCandidate c) => new()
	{
		TimestampMs = c.Timestamp.ToUnixTimeMilliseconds(),
		Level = (int)c.Level,
		MessageTemplate = c.MessageTemplate,
		Message = c.Message,
		Exception = c.Exception,
		TraceId = c.TraceId,
		SpanId = c.SpanId,
		EventId = c.EventId,
		TemplateHash = StableHash(c.MessageTemplate),
		PropertiesJson = SerializeProperties(c.Properties),
	};

	static string SerializeProperties(ImmutableDictionary<string, JsonElement> props)
	{
		if (props.IsEmpty)
			return "{}";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			foreach (var (k, v) in props)
			{
				writer.WritePropertyName(k);
				v.WriteTo(writer);
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	static long StableHash(string s)
	{
		// FNV-1a 64-bit — stable across runs (unlike string.GetHashCode).
		const long offset = unchecked((long)14695981039346656037);
		const long prime = 1099511628211;
		var h = offset;
		foreach (var c in s)
		{
			h ^= c;
			h *= prime;
		}
		return h;
	}
}
