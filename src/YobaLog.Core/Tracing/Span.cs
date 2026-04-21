using System.Collections.Immutable;
using System.Text.Json;

namespace YobaLog.Core.Tracing;

// Immutable domain type for a completed trace span. Shape is shared across ingestion and
// query paths — unlike LogEvent/LogEventCandidate there's no pre/post-insert distinction
// because SpanId is assigned client-side by the emitter (Activity.SpanId is populated on
// Activity creation, well before it reaches us).
//
// Attribute bag / events / links are JSON-serialized at the storage boundary
// (AttributesJson / EventsJson / LinksJson columns) — same dynamic-bag approach as
// LogEvent.Properties. Schema in SpansTable.Definition.
public sealed record Span(
	string SpanId,
	string TraceId,
	string? ParentSpanId,
	string Name,
	SpanKind Kind,
	DateTimeOffset StartTime,
	TimeSpan Duration,
	SpanStatusCode Status,
	string? StatusDescription,
	ImmutableDictionary<string, JsonElement> Attributes,
	ImmutableArray<SpanEvent> Events,
	ImmutableArray<SpanLink> Links);

// Mirror of OTel's SpanKind. Integer values match the proto enum so the OTLP ingest path
// can cast directly without a mapping table.
public enum SpanKind
{
	Internal = 0,
	Server = 1,
	Client = 2,
	Producer = 3,
	Consumer = 4,
}

// Mirror of OTel's Span.Status.StatusCode. System.Diagnostics.ActivityStatusCode happens to
// use the same three values (Unset=0, Ok=1, Error=2) — intentional alignment by MS — but we
// define our own enum to avoid leaking Activity into Core's public surface.
public enum SpanStatusCode
{
	Unset = 0,
	Ok = 1,
	Error = 2,
}

public sealed record SpanEvent(
	DateTimeOffset Timestamp,
	string Name,
	ImmutableDictionary<string, JsonElement> Attributes);

public sealed record SpanLink(
	string TraceId,
	string SpanId,
	ImmutableDictionary<string, JsonElement> Attributes);
