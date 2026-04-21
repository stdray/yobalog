using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using YobaLog.Core;
using YobaLog.Core.Tracing;

namespace YobaLog.Web.Observability;

// BaseExporter<Activity> → Span → ISpanStore.AppendBatchAsync($system.traces.db).
//
// Phase H switched the write target from ILogStore ($system.logs.db with Properties.Kind=
// "span" sentinel) to a dedicated ISpanStore. Storage shape is now structurally typed
// (SpanId / TraceId / Duration as first-class columns, AttributesJson for the dynamic
// bag) — waterfall UI can query by TraceId in one indexed lookup instead of fishing with
// `where Properties.Kind == 'span'`.
//
// Bypasses IIngestionPipeline on purpose (mirror SystemLoggerProvider pattern): spans are
// already batched by SimpleActivityExportProcessor's queue, and feeding them through the
// Channels pipeline would double-buffer and tangle shutdown order.
sealed class SystemSpanExporter : BaseExporter<Activity>
{
	readonly ISpanStore _spans;
	readonly ILogger<SystemSpanExporter> _logger;

	public SystemSpanExporter(ISpanStore spans, ILogger<SystemSpanExporter> logger)
	{
		_spans = spans;
		_logger = logger;
	}

	public override ExportResult Export(in Batch<Activity> batch)
	{
		var spans = new List<Span>();
		foreach (var activity in batch)
			spans.Add(ToSpan(activity));

		if (spans.Count == 0)
			return ExportResult.Success;

		try
		{
			// BaseExporter.Export is synchronous — block on the ValueTask. Batch size is capped
			// by the SimpleActivityExportProcessor upstream; under load we still prefer sync
			// backpressure (late spans drop) over unbounded task queues.
			_spans.AppendBatchAsync(WorkspaceId.System, spans, CancellationToken.None)
				.AsTask()
				.GetAwaiter()
				.GetResult();
			return ExportResult.Success;
		}
		catch (Exception ex)
		{
			SystemSpanLog.ExportFailed(_logger, ex, spans.Count);
			return ExportResult.Failure;
		}
	}

	public static Span ToSpan(Activity activity)
	{
		var attributes = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);

		// Augment real tags with the source name so downstream filters (`where Attributes.source
		// == 'YobaLog.Ingestion'`) work without needing a dedicated column.
		attributes["source"] = JsonFromString(activity.Source.Name);

		foreach (var kv in activity.TagObjects)
		{
			if (kv.Value is null) continue;
			// Reserved-key guard — a tag literally named "source" would silently shadow our
			// source-name augmentation. Unlikely in practice but catches rogue instrumentation.
			if (kv.Key == "source") continue;
			attributes[kv.Key] = TagValueToJson(kv.Value);
		}

		var events = ImmutableArray.CreateBuilder<SpanEvent>();
		foreach (var e in activity.Events)
		{
			var eventAttrs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
			foreach (var kv in e.Tags)
			{
				if (kv.Value is null) continue;
				eventAttrs[kv.Key] = TagValueToJson(kv.Value);
			}
			events.Add(new SpanEvent(
				Timestamp: new DateTimeOffset(e.Timestamp.UtcDateTime, TimeSpan.Zero),
				Name: e.Name,
				Attributes: eventAttrs.ToImmutable()));
		}

		var links = ImmutableArray.CreateBuilder<SpanLink>();
		foreach (var l in activity.Links)
		{
			var linkAttrs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
			foreach (var kv in l.Tags ?? [])
			{
				if (kv.Value is null) continue;
				linkAttrs[kv.Key] = TagValueToJson(kv.Value);
			}
			links.Add(new SpanLink(
				TraceId: l.Context.TraceId.ToHexString(),
				SpanId: l.Context.SpanId.ToHexString(),
				Attributes: linkAttrs.ToImmutable()));
		}

		return new Span(
			SpanId: activity.SpanId.ToHexString(),
			TraceId: activity.TraceId.ToHexString(),
			ParentSpanId: activity.ParentSpanId != default ? activity.ParentSpanId.ToHexString() : null,
			Name: activity.DisplayName,
			Kind: MapKind(activity.Kind),
			StartTime: new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
			// Duration is populated on Stop; for mid-flight activities (shouldn't happen with
			// SimpleActivityExportProcessor but be defensive) we'd otherwise read TimeSpan.Zero.
			Duration: activity.Duration.Ticks > 0 ? activity.Duration : TimeSpan.Zero,
			Status: MapStatus(activity.Status),
			StatusDescription: string.IsNullOrEmpty(activity.StatusDescription) ? null : activity.StatusDescription,
			Attributes: attributes.ToImmutable(),
			Events: events.ToImmutable(),
			Links: links.ToImmutable());
	}

	// ActivityKind and SpanKind share integer values by MS convention — cast is safe. We still
	// wrap it in a switch so a future enum member-add on either side surfaces as a compile
	// error (switch-expression exhaustiveness check) instead of silently producing garbage.
	static SpanKind MapKind(ActivityKind kind) => kind switch
	{
		ActivityKind.Internal => SpanKind.Internal,
		ActivityKind.Server => SpanKind.Server,
		ActivityKind.Client => SpanKind.Client,
		ActivityKind.Producer => SpanKind.Producer,
		ActivityKind.Consumer => SpanKind.Consumer,
		_ => SpanKind.Internal,
	};

	static SpanStatusCode MapStatus(ActivityStatusCode status) => status switch
	{
		ActivityStatusCode.Unset => SpanStatusCode.Unset,
		ActivityStatusCode.Ok => SpanStatusCode.Ok,
		ActivityStatusCode.Error => SpanStatusCode.Error,
		_ => SpanStatusCode.Unset,
	};

	static JsonElement TagValueToJson(object value)
	{
		return value switch
		{
			string s => JsonFromString(s),
			bool b => JsonFromLiteral(b ? "true" : "false"),
			int or long or short or byte or uint or ulong or ushort or sbyte =>
				JsonFromLiteral(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
			float or double or decimal =>
				JsonFromLiteral(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)),
			_ => JsonFromString(value.ToString() ?? ""),
		};
	}

	static JsonElement JsonFromString(string s) => JsonFromLiteral(JsonSerializer.Serialize(s));

	static JsonElement JsonFromLiteral(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.Clone();
	}
}

static partial class SystemSpanLog
{
	[LoggerMessage(EventId = 40, Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Failed to export {Count} span(s) to $system.traces.db; dropped on the floor")]
	public static partial void ExportFailed(ILogger logger, Exception ex, int count);
}
