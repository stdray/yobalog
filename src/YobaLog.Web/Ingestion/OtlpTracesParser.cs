using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using YobaLog.Core.Tracing;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ProtoSpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using DomainSpan = YobaLog.Core.Tracing.Span;
using DomainSpanKind = YobaLog.Core.Tracing.SpanKind;

namespace YobaLog.Web.Ingestion;

// OTLP Traces (HTTP/Protobuf) → Span[]. Decision-log 2026-04-21 Phase H mapping: trace_id
// and span_id/parent_span_id hex-encoded, all-zero parent = root span (null), resource and
// scope attributes merged into each span's attributes with resource-wins-on-collision.
//
// Invariant (Phase F Rule 1 of decision-log): proto DTOs never leak past this file.
// Everything downstream sees YobaLog.Core.Tracing.Span.
static class OtlpTracesParser
{
	public static OtlpTracesParseResult Parse(ReadOnlySpan<byte> protobuf)
	{
		ExportTraceServiceRequest request;
		try
		{
			request = ExportTraceServiceRequest.Parser.ParseFrom(protobuf);
		}
		catch (InvalidProtocolBufferException)
		{
			return OtlpTracesParseResult.Malformed;
		}

		var spans = new List<DomainSpan>();
		var errors = 0;

		foreach (var resourceSpans in request.ResourceSpans)
		{
			var resourceAttrs = FlattenAttributes(resourceSpans.Resource?.Attributes);
			foreach (var scopeSpans in resourceSpans.ScopeSpans)
			{
				var scopeAttrs = FlattenAttributes(scopeSpans.Scope?.Attributes);
				foreach (var protoSpan in scopeSpans.Spans)
				{
					if (TryConvert(protoSpan, resourceAttrs, scopeAttrs, out var span))
						spans.Add(span);
					else
						errors++;
				}
			}
		}

		return new OtlpTracesParseResult(spans, errors, IsMalformed: false);
	}

	static bool TryConvert(
		ProtoSpan protoSpan,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs,
		out DomainSpan span)
	{
		span = null!;

		// trace_id is 16 bytes, span_id is 8. All-zero = invalid by OTel spec — can't render
		// a waterfall without a real key.
		if (protoSpan.TraceId.IsEmpty || AllZero(protoSpan.TraceId.Span)) return false;
		if (protoSpan.SpanId.IsEmpty || AllZero(protoSpan.SpanId.Span)) return false;
		if (protoSpan.StartTimeUnixNano == 0) return false;

		var startTime = FromUnixNanos(protoSpan.StartTimeUnixNano);
		// end_time may be zero on in-flight spans (shouldn't reach us via export, but be defensive).
		var duration = protoSpan.EndTimeUnixNano > protoSpan.StartTimeUnixNano
			? FromUnixNanos(protoSpan.EndTimeUnixNano) - startTime
			: TimeSpan.Zero;

		// Merge order: span attrs < scope attrs < resource attrs. Resource identity
		// (service.name / host.name / …) must win on collision — a span can't override
		// deployment metadata.
		var attributes = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var kv in FlattenAttributes(protoSpan.Attributes))
			attributes[kv.Key] = kv.Value;
		foreach (var kv in scopeAttrs)
			attributes[kv.Key] = kv.Value;
		foreach (var kv in resourceAttrs)
			attributes[kv.Key] = kv.Value;

		if (protoSpan.DroppedAttributesCount != 0)
			attributes["otlp_dropped_attributes"] = JsonNumber(protoSpan.DroppedAttributesCount);
		if (protoSpan.DroppedEventsCount != 0)
			attributes["otlp_dropped_events"] = JsonNumber(protoSpan.DroppedEventsCount);
		if (protoSpan.DroppedLinksCount != 0)
			attributes["otlp_dropped_links"] = JsonNumber(protoSpan.DroppedLinksCount);

		span = new DomainSpan(
			SpanId: Convert.ToHexStringLower(protoSpan.SpanId.Span),
			TraceId: Convert.ToHexStringLower(protoSpan.TraceId.Span),
			ParentSpanId: protoSpan.ParentSpanId.IsEmpty || AllZero(protoSpan.ParentSpanId.Span)
				? null
				: Convert.ToHexStringLower(protoSpan.ParentSpanId.Span),
			Name: protoSpan.Name,
			Kind: MapKind(protoSpan.Kind),
			StartTime: startTime,
			Duration: duration,
			Status: protoSpan.Status is null ? SpanStatusCode.Unset : MapStatus(protoSpan.Status.Code),
			StatusDescription: protoSpan.Status?.Message is { Length: > 0 } msg ? msg : null,
			Attributes: attributes.ToImmutable(),
			Events: ConvertEvents(protoSpan.Events),
			Links: ConvertLinks(protoSpan.Links));
		return true;
	}

	// OTLP proto is 1-indexed with a leading Unspecified (0). Our domain enum is 0-indexed
	// matching ActivityKind. Map explicitly — a direct (int) cast would shift every value.
	static DomainSpanKind MapKind(ProtoSpanKind kind) => kind switch
	{
		ProtoSpanKind.Internal => DomainSpanKind.Internal,
		ProtoSpanKind.Server => DomainSpanKind.Server,
		ProtoSpanKind.Client => DomainSpanKind.Client,
		ProtoSpanKind.Producer => DomainSpanKind.Producer,
		ProtoSpanKind.Consumer => DomainSpanKind.Consumer,
		// OTLP spec says treat Unspecified as Internal ("Implementations MAY assume SpanKind
		// to be INTERNAL when receiving UNSPECIFIED").
		_ => DomainSpanKind.Internal,
	};

	// OTLP StatusCode shares integer values with our SpanStatusCode (Unset=0 / Ok=1 / Error=2)
	// by MS/OTel alignment — cast via switch for exhaustiveness check rather than a raw cast.
	static SpanStatusCode MapStatus(Status.Types.StatusCode code) => code switch
	{
		Status.Types.StatusCode.Unset => SpanStatusCode.Unset,
		Status.Types.StatusCode.Ok => SpanStatusCode.Ok,
		Status.Types.StatusCode.Error => SpanStatusCode.Error,
		_ => SpanStatusCode.Unset,
	};

	static ImmutableArray<SpanEvent> ConvertEvents(Google.Protobuf.Collections.RepeatedField<ProtoSpan.Types.Event> events)
	{
		if (events.Count == 0) return [];
		var builder = ImmutableArray.CreateBuilder<SpanEvent>(events.Count);
		foreach (var e in events)
		{
			builder.Add(new SpanEvent(
				Timestamp: FromUnixNanos(e.TimeUnixNano),
				Name: e.Name,
				Attributes: FlattenAttributes(e.Attributes)));
		}
		return builder.ToImmutable();
	}

	static ImmutableArray<SpanLink> ConvertLinks(Google.Protobuf.Collections.RepeatedField<ProtoSpan.Types.Link> links)
	{
		if (links.Count == 0) return [];
		var builder = ImmutableArray.CreateBuilder<SpanLink>(links.Count);
		foreach (var l in links)
		{
			builder.Add(new SpanLink(
				TraceId: Convert.ToHexStringLower(l.TraceId.Span),
				SpanId: Convert.ToHexStringLower(l.SpanId.Span),
				Attributes: FlattenAttributes(l.Attributes)));
		}
		return builder.ToImmutable();
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

	static JsonElement AnyValueToJson(AnyValue? value)
	{
		if (value is null) return JsonNull();

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

	static bool AllZero(ReadOnlySpan<byte> bytes)
	{
		for (var i = 0; i < bytes.Length; i++)
			if (bytes[i] != 0) return false;
		return true;
	}

	static DateTimeOffset FromUnixNanos(ulong unixNs)
	{
		var ms = (long)(unixNs / 1_000_000UL);
		var subMs = (long)(unixNs % 1_000_000UL);
		return DateTimeOffset.FromUnixTimeMilliseconds(ms).AddTicks(subMs / 100L);
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

sealed record OtlpTracesParseResult(
	IReadOnlyList<YobaLog.Core.Tracing.Span> Spans,
	int Errors,
	bool IsMalformed)
{
	public static readonly OtlpTracesParseResult Malformed = new([], 0, IsMalformed: true);
}
