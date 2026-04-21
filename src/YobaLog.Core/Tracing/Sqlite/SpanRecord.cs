using System.Collections.Immutable;
using System.Text.Json;
using LinqToDB.Mapping;

namespace YobaLog.Core.Tracing.Sqlite;

// Storage shape for spans. Nanosecond precision retained via int64 (up to 292 years of
// nanoseconds fits in signed int64). Attributes/Events/Links blobbed as JSON for flexibility
// — query-path KQL over attributes uses json_extract (same pattern as Events.PropertiesJson).
[Table("Spans")]
sealed class SpanRecord
{
	[Column, PrimaryKey] public string SpanId { get; set; } = "";
	[Column, NotNull] public string TraceId { get; set; } = "";
	[Column, Nullable] public string? ParentSpanId { get; set; }
	[Column, NotNull] public string Name { get; set; } = "";
	[Column, NotNull] public int Kind { get; set; }
	[Column, NotNull] public long StartUnixNs { get; set; }
	[Column, NotNull] public long EndUnixNs { get; set; }
	[Column, NotNull] public int StatusCode { get; set; }
	[Column, Nullable] public string? StatusDescription { get; set; }
	[Column, NotNull] public string AttributesJson { get; set; } = "{}";
	[Column, NotNull] public string EventsJson { get; set; } = "[]";
	[Column, NotNull] public string LinksJson { get; set; } = "[]";

	public static SpanRecord FromSpan(Span span) => new()
	{
		SpanId = span.SpanId,
		TraceId = span.TraceId,
		ParentSpanId = span.ParentSpanId,
		Name = span.Name,
		Kind = (int)span.Kind,
		StartUnixNs = ToUnixNanos(span.StartTime),
		EndUnixNs = ToUnixNanos(span.StartTime + span.Duration),
		StatusCode = (int)span.Status,
		StatusDescription = span.StatusDescription,
		AttributesJson = PropertiesJsonSerializer.Serialize(span.Attributes),
		EventsJson = SerializeEvents(span.Events),
		LinksJson = SerializeLinks(span.Links),
	};

	public Span ToSpan()
	{
		var start = FromUnixNanos(StartUnixNs);
		var end = FromUnixNanos(EndUnixNs);
		return new Span(
			SpanId: SpanId,
			TraceId: TraceId,
			ParentSpanId: ParentSpanId,
			Name: Name,
			Kind: (SpanKind)Kind,
			StartTime: start,
			Duration: end - start,
			Status: (SpanStatusCode)StatusCode,
			StatusDescription: StatusDescription,
			Attributes: DeserializeAttributes(AttributesJson),
			Events: DeserializeEvents(EventsJson),
			Links: DeserializeLinks(LinksJson));
	}

	static long ToUnixNanos(DateTimeOffset dto)
	{
		// 100 ns per tick; DateTimeOffset can't represent sub-100ns precision natively.
		return dto.ToUnixTimeMilliseconds() * 1_000_000L
			+ (dto.UtcTicks % TimeSpan.TicksPerMillisecond) * 100L;
	}

	static DateTimeOffset FromUnixNanos(long unixNs)
	{
		var ms = unixNs / 1_000_000L;
		var subMs = unixNs % 1_000_000L;
		return DateTimeOffset.FromUnixTimeMilliseconds(ms).AddTicks(subMs / 100L);
	}

	static string SerializeEvents(ImmutableArray<SpanEvent> events)
	{
		if (events.IsDefaultOrEmpty) return "[]";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartArray();
			foreach (var e in events)
			{
				writer.WriteStartObject();
				writer.WriteNumber("t", e.Timestamp.ToUnixTimeMilliseconds());
				writer.WriteString("n", e.Name);
				writer.WritePropertyName("a");
				JsonSerializer.Serialize(writer, e.Attributes);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}
		return System.Text.Encoding.UTF8.GetString(stream.ToArray());
	}

	static string SerializeLinks(ImmutableArray<SpanLink> links)
	{
		if (links.IsDefaultOrEmpty) return "[]";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartArray();
			foreach (var l in links)
			{
				writer.WriteStartObject();
				writer.WriteString("tid", l.TraceId);
				writer.WriteString("sid", l.SpanId);
				writer.WritePropertyName("a");
				JsonSerializer.Serialize(writer, l.Attributes);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}
		return System.Text.Encoding.UTF8.GetString(stream.ToArray());
	}

	static ImmutableDictionary<string, JsonElement> DeserializeAttributes(string json)
	{
		if (string.IsNullOrEmpty(json) || json == "{}")
			return ImmutableDictionary<string, JsonElement>.Empty;
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Object)
			return ImmutableDictionary<string, JsonElement>.Empty;
		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var prop in doc.RootElement.EnumerateObject())
			builder[prop.Name] = prop.Value.Clone();
		return builder.ToImmutable();
	}

	static ImmutableArray<SpanEvent> DeserializeEvents(string json)
	{
		if (string.IsNullOrEmpty(json) || json == "[]")
			return [];
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array)
			return [];
		var builder = ImmutableArray.CreateBuilder<SpanEvent>(doc.RootElement.GetArrayLength());
		foreach (var e in doc.RootElement.EnumerateArray())
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(e.GetProperty("t").GetInt64());
			var name = e.GetProperty("n").GetString() ?? "";
			var attrs = DeserializeAttributes(e.GetProperty("a").GetRawText());
			builder.Add(new SpanEvent(ts, name, attrs));
		}
		return builder.ToImmutable();
	}

	static ImmutableArray<SpanLink> DeserializeLinks(string json)
	{
		if (string.IsNullOrEmpty(json) || json == "[]")
			return [];
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array)
			return [];
		var builder = ImmutableArray.CreateBuilder<SpanLink>(doc.RootElement.GetArrayLength());
		foreach (var l in doc.RootElement.EnumerateArray())
		{
			var tid = l.GetProperty("tid").GetString() ?? "";
			var sid = l.GetProperty("sid").GetString() ?? "";
			var attrs = DeserializeAttributes(l.GetProperty("a").GetRawText());
			builder.Add(new SpanLink(tid, sid, attrs));
		}
		return builder.ToImmutable();
	}
}
