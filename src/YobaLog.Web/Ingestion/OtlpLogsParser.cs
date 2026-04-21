using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using YobaLog.Core;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web.Ingestion;

// OTLP Logs (HTTP/Protobuf) → LogEventCandidate batch. Mapping per decision-log 2026-04-21.
//
// Invariant (Rule 1 of the same entry): proto DTOs never leak past this file. Everything
// downstream speaks LogEventCandidate.
static class OtlpLogsParser
{
	public static OtlpLogsParseResult Parse(ReadOnlySpan<byte> protobuf)
	{
		ExportLogsServiceRequest request;
		try
		{
			request = ExportLogsServiceRequest.Parser.ParseFrom(protobuf);
		}
		catch (InvalidProtocolBufferException)
		{
			return OtlpLogsParseResult.Malformed;
		}

		var candidates = new List<LogEventCandidate>();
		var errors = 0;

		foreach (var resourceLogs in request.ResourceLogs)
		{
			var resourceAttrs = FlattenAttributes(resourceLogs.Resource?.Attributes);
			foreach (var scopeLogs in resourceLogs.ScopeLogs)
			{
				foreach (var record in scopeLogs.LogRecords)
				{
					if (TryConvert(record, resourceAttrs, out var candidate))
						candidates.Add(candidate);
					else
						errors++;
				}
			}
		}

		return new OtlpLogsParseResult(candidates, errors, IsMalformed: false);
	}

	static bool TryConvert(
		LogRecord record,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		out LogEventCandidate candidate)
	{
		candidate = null!;

		// time_unix_nano is primary; observed_time_unix_nano is the fallback per OTel spec
		// (used when the producer doesn't control event time, e.g. scraped log files).
		// Both 0 = bogus record — reject rather than inventing DateTimeOffset.UtcNow,
		// which would silently collapse batches of missing-timestamp events to "now".
		var unixNs = record.TimeUnixNano != 0 ? record.TimeUnixNano : record.ObservedTimeUnixNano;
		if (unixNs == 0) return false;

		var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixNs / 1_000_000));

		var level = MapSeverity(record.SeverityNumber);
		var body = FormatBody(record.Body);

		// event_name is new in OTLP 1.5 — equivalent to CLEF's @mt (template name). When
		// absent we reuse the body as a template, matching seq-logging's behavior for
		// template-less events.
		var messageTemplate = string.IsNullOrEmpty(record.EventName) ? body : record.EventName;

		var properties = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);

		// Attribute merge order: record < resource. Resource attributes describe the
		// deployment identity (service.name, host.name, ...) and must win on collision —
		// a record can't override what container it came from.
		foreach (var kv in FlattenAttributes(record.Attributes))
			properties[kv.Key] = kv.Value;
		foreach (var kv in resourceAttrs)
			properties[kv.Key] = kv.Value;

		if (!string.IsNullOrEmpty(record.SeverityText))
			properties["severity_text"] = JsonString(record.SeverityText);
		if (record.DroppedAttributesCount != 0)
			properties["otlp_dropped"] = JsonNumber(record.DroppedAttributesCount);
		if (record.Flags != 0)
			properties["otlp_flags"] = JsonNumber(record.Flags);

		var traceId = HexOrNull(record.TraceId.Span);
		var spanId = HexOrNull(record.SpanId.Span);

		candidate = new LogEventCandidate(
			timestamp,
			level,
			messageTemplate,
			body,
			Exception: null,
			TraceId: traceId,
			SpanId: spanId,
			EventId: null,
			Properties: properties.ToImmutable());
		return true;
	}

	// OTel SeverityNumber is a 1-24 ladder (4 steps per level). Map to the 6-level CLEF
	// ladder by integer division. Unspecified (0) defaults to Information — matches what
	// clients like python-logging and log4j do when severity is unset.
	static LogLevel MapSeverity(SeverityNumber severity) => severity switch
	{
		>= SeverityNumber.Trace and <= SeverityNumber.Trace4 => LogLevel.Verbose,
		>= SeverityNumber.Debug and <= SeverityNumber.Debug4 => LogLevel.Debug,
		>= SeverityNumber.Info and <= SeverityNumber.Info4 => LogLevel.Information,
		>= SeverityNumber.Warn and <= SeverityNumber.Warn4 => LogLevel.Warning,
		>= SeverityNumber.Error and <= SeverityNumber.Error4 => LogLevel.Error,
		>= SeverityNumber.Fatal and <= SeverityNumber.Fatal4 => LogLevel.Fatal,
		_ => LogLevel.Information,
	};

	static string FormatBody(AnyValue? body)
	{
		if (body is null) return string.Empty;
		return body.ValueCase switch
		{
			AnyValue.ValueOneofCase.StringValue => body.StringValue,
			AnyValue.ValueOneofCase.BoolValue => body.BoolValue ? "true" : "false",
			AnyValue.ValueOneofCase.IntValue => body.IntValue.ToString(CultureInfo.InvariantCulture),
			AnyValue.ValueOneofCase.DoubleValue => body.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
			AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(body.BytesValue.Span),
			AnyValue.ValueOneofCase.ArrayValue or AnyValue.ValueOneofCase.KvlistValue =>
				AnyValueToJson(body).GetRawText(),
			_ => string.Empty,
		};
	}

	static ImmutableDictionary<string, JsonElement> FlattenAttributes(
		Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes)
	{
		if (attributes is null || attributes.Count == 0)
			return ImmutableDictionary<string, JsonElement>.Empty;

		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var kv in attributes)
		{
			if (string.IsNullOrEmpty(kv.Key)) continue;
			builder[kv.Key] = AnyValueToJson(kv.Value);
		}
		return builder.ToImmutable();
	}

	// AnyValue → JsonElement. Complex values (Array / Kvlist) are serialized into JSON
	// and parsed back so downstream code sees a normal JsonElement tree. The round-trip
	// costs an allocation but keeps the Properties bag shape identical to CLEF parsing.
	static JsonElement AnyValueToJson(AnyValue? value)
	{
		if (value is null)
			return JsonNull();

		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
			WriteAnyValue(writer, value);

		using var doc = JsonDocument.Parse(buffer.WrittenMemory);
		return doc.RootElement.Clone();
	}

	static void WriteAnyValue(Utf8JsonWriter writer, AnyValue value)
	{
		switch (value.ValueCase)
		{
			case AnyValue.ValueOneofCase.StringValue:
				writer.WriteStringValue(value.StringValue);
				break;
			case AnyValue.ValueOneofCase.BoolValue:
				writer.WriteBooleanValue(value.BoolValue);
				break;
			case AnyValue.ValueOneofCase.IntValue:
				writer.WriteNumberValue(value.IntValue);
				break;
			case AnyValue.ValueOneofCase.DoubleValue:
				writer.WriteNumberValue(value.DoubleValue);
				break;
			case AnyValue.ValueOneofCase.BytesValue:
				writer.WriteStringValue(Convert.ToBase64String(value.BytesValue.Span));
				break;
			case AnyValue.ValueOneofCase.ArrayValue:
				writer.WriteStartArray();
				foreach (var item in value.ArrayValue.Values)
					WriteAnyValue(writer, item);
				writer.WriteEndArray();
				break;
			case AnyValue.ValueOneofCase.KvlistValue:
				writer.WriteStartObject();
				foreach (var kv in value.KvlistValue.Values)
				{
					writer.WritePropertyName(kv.Key);
					WriteAnyValue(writer, kv.Value);
				}
				writer.WriteEndObject();
				break;
			default:
				writer.WriteNullValue();
				break;
		}
	}

	// trace_id is fixed 16 bytes, span_id is 8. OTel treats all-zero as "absent"
	// (traceparent header convention) — emitting the hex would pollute downstream joins
	// on TraceId with a magic constant.
	static string? HexOrNull(ReadOnlySpan<byte> bytes)
	{
		if (bytes.IsEmpty) return null;
		var allZero = true;
		for (var i = 0; i < bytes.Length; i++)
		{
			if (bytes[i] != 0) { allZero = false; break; }
		}
		return allZero ? null : Convert.ToHexStringLower(bytes);
	}

	static JsonElement JsonString(string s)
	{
		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
		return doc.RootElement.Clone();
	}

	static JsonElement JsonNumber(uint n)
	{
		using var doc = JsonDocument.Parse(n.ToString(CultureInfo.InvariantCulture));
		return doc.RootElement.Clone();
	}

	static JsonElement JsonNull()
	{
		using var doc = JsonDocument.Parse("null");
		return doc.RootElement.Clone();
	}
}

sealed record OtlpLogsParseResult(
	IReadOnlyList<LogEventCandidate> Candidates,
	int Errors,
	bool IsMalformed)
{
	public static readonly OtlpLogsParseResult Malformed = new([], 0, IsMalformed: true);
}
