using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using YobaLog.Core;
using YobaLog.Core.SelfLogging;
using YobaLog.Core.Storage;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web.Observability;

// BaseExporter<Activity> → LogEventCandidate → ILogStore.AppendBatchAsync($system).
//
// Bypasses IIngestionPipeline on purpose (mirror SystemLoggerProvider pattern): spans are
// already batched by SimpleActivityExportProcessor's queue, and feeding them through the
// Channels-based pipeline would double-buffer and tangle shutdown order. Direct write is
// what $system is for.
//
// Phase G "interim storage": until Phase H ships ISpanStore, spans live in $system.logs.db
// under Properties.Kind="span" + flattened parent/duration/start/status. KQL over them is
// awkward ('where Properties.DurationNs > ...'), but that's fine — Phase G is
// self-observability, not user-facing trace UI (decision-log 2026-04-21 Phase G↔H sequencing).
sealed class SystemSpanExporter : BaseExporter<Activity>
{
	readonly ILogStore _store;
	readonly ILogger<SystemSpanExporter> _logger;

	public SystemSpanExporter(ILogStore store, ILogger<SystemSpanExporter> logger)
	{
		_store = store;
		_logger = logger;
	}

	public override ExportResult Export(in Batch<Activity> batch)
	{
		var candidates = new List<LogEventCandidate>();
		foreach (var activity in batch)
			candidates.Add(ToCandidate(activity));

		if (candidates.Count == 0)
			return ExportResult.Success;

		try
		{
			// BaseExporter.Export is synchronous — block on the ValueTask. Batch size is capped
			// by the SimpleActivityExportProcessor upstream; under load we still prefer sync
			// backpressure (late spans drop) over unbounded task queues.
			_store.AppendBatchAsync(WorkspaceId.System, candidates, CancellationToken.None)
				.AsTask()
				.GetAwaiter()
				.GetResult();
			return ExportResult.Success;
		}
		catch (Exception ex)
		{
			SystemSpanLog.ExportFailed(_logger, ex, candidates.Count);
			return ExportResult.Failure;
		}
	}

	public static LogEventCandidate ToCandidate(Activity activity)
	{
		var props = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		props["Kind"] = JsonFromLiteral("\"span\"");
		props["Name"] = JsonFromString(activity.DisplayName);
		props["ActivityKind"] = JsonFromString(activity.Kind.ToString());
		props["Source"] = JsonFromString(activity.Source.Name);

		// Nanosecond precision isn't on DateTimeOffset — construct from Ticks (100ns units).
		var startUnixNs = ((DateTimeOffset)activity.StartTimeUtc).ToUnixTimeMilliseconds() * 1_000_000L
			+ (activity.StartTimeUtc.Ticks % TimeSpan.TicksPerMillisecond) * 100L;
		props["StartUnixNs"] = JsonFromLiteral(startUnixNs.ToString(CultureInfo.InvariantCulture));

		// Activity.Duration is populated on dispose; for exporter path (post-dispose) it's accurate.
		// Falling back to 0 handles the niche case of exporting an in-flight activity (shouldn't
		// happen in practice with SimpleActivityExportProcessor, but be defensive).
		var durationNs = activity.Duration.Ticks > 0 ? activity.Duration.Ticks * 100 : 0;
		props["DurationNs"] = JsonFromLiteral(durationNs.ToString(CultureInfo.InvariantCulture));

		if (activity.ParentSpanId != default)
			props["ParentSpanId"] = JsonFromString(activity.ParentSpanId.ToHexString());

		if (activity.Status != ActivityStatusCode.Unset)
			props["StatusCode"] = JsonFromString(activity.Status.ToString());
		if (!string.IsNullOrEmpty(activity.StatusDescription))
			props["StatusDescription"] = JsonFromString(activity.StatusDescription);

		foreach (var kv in activity.TagObjects)
		{
			if (kv.Value is null) continue;
			// Skip tag names that collide with our flattened span fields — a "Kind" tag from the
			// instrumented code would silently shadow our Kind="span" sentinel, making the event
			// not look like a span to downstream filters.
			if (kv.Key is "Kind" or "Name" or "ActivityKind" or "Source" or "StartUnixNs"
				or "DurationNs" or "ParentSpanId" or "StatusCode" or "StatusDescription")
			{
				continue;
			}
			props[kv.Key] = TagValueToJson(kv.Value);
		}

		return new LogEventCandidate(
			Timestamp: new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
			Level: LogLevel.Information,
			MessageTemplate: activity.DisplayName,
			Message: activity.DisplayName,
			Exception: null,
			TraceId: activity.TraceId.ToHexString(),
			SpanId: activity.SpanId.ToHexString(),
			EventId: null,
			Properties: props.ToImmutable());
	}

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
		Message = "Failed to export {Count} span(s) to $system; dropped on the floor")]
	public static partial void ExportFailed(ILogger logger, Exception ex, int count);
}
