using System.Text.Json;
using YobaLog.Core;
using YobaLog.Core.Auth;
using YobaLog.Core.Ingestion;
using YobaLog.Web.Ingestion;

namespace YobaLog.Web;

// Format-specific handlers for POST /api/v1/ingest/<format>. Each one:
//   1. validates the X-Seq-ApiKey (header or ?apiKey=) and resolves the workspace scope;
//   2. parses the request body into LogEventCandidates;
//   3. hands the batch to IIngestionPipeline.IngestAsync.
//
// Auth and pipeline-dispatch are shared via ResolveScopeAsync / CompleteAsync; only the
// body-parsing step is format-specific. Adding GELF means writing one more handler like CleF.
static class IngestionHandlers
{
	public static async Task<IResult> CleF(
		HttpContext ctx,
		IApiKeyStore apiKeys,
		IIngestionPipeline pipeline,
		ICleFParser parser,
		CancellationToken ct)
	{
		var scope = await ResolveScopeAsync(ctx, apiKeys, ct);
		if (scope is null)
			return Results.Unauthorized();

		var candidates = new List<LogEventCandidate>();
		var errorCount = 0;

		// Two wire formats share this handler:
		//   1. application/vnd.serilog.clef (or unspecified): NDJSON — one CLEF event per line.
		//   2. application/json: `{"Events":[{…},{…}]}` envelope (seq-logging 3.x, @datalust/winston-seq).
		// Inner events are either CLEF (`@t`, `@l`, …) or seq-logging's legacy Raw shape
		// (`Timestamp`, `Level`, `MessageTemplate`, `Exception`, `Properties`). Raw is normalized
		// to CLEF via RawEventEnvelope.ToClefLine.
		var contentType = ctx.Request.ContentType ?? "";
		var isJsonEnvelope = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
			&& !contentType.Contains("clef", StringComparison.OrdinalIgnoreCase);

		if (isJsonEnvelope)
		{
			using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("Events", out var events)
				|| events.ValueKind != JsonValueKind.Array)
			{
				return Results.BadRequest("expected {\"Events\": [...]} envelope for application/json");
			}

			var idx = 0;
			foreach (var evt in events.EnumerateArray())
			{
				idx++;
				var lineJson = evt.TryGetProperty("@t", out _)
					? evt.GetRawText()
					: RawEventEnvelope.ToClefLine(evt);
				AccumulateResult(parser.ParseLine(lineJson, idx), candidates, ref errorCount);
			}
		}
		else
		{
			await foreach (var line in parser.ParseAsync(ctx.Request.Body, ct))
				AccumulateResult(line, candidates, ref errorCount);
		}

		if (candidates.Count > 0)
			await pipeline.IngestAsync(scope.Value, candidates, ct);

		return Results.Created(ctx.Request.Path.Value, new IngestResponse(candidates.Count, errorCount));
	}

	// OTLP Logs ingestion: HTTP/Protobuf body parse → same CompositeApiKeyStore auth + same
	// IIngestionPipeline as CLEF. Decision-log 2026-04-21 (Phase F) for the mapping table.
	// Wire-format decoding lives in OtlpLogsParser; proto DTOs never escape this function.
	public static async Task<IResult> OtlpLogs(
		HttpContext ctx,
		IApiKeyStore apiKeys,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var scope = await ResolveScopeAsync(ctx, apiKeys, ct);
		if (scope is null)
			return Results.Unauthorized();

		// Buffer the whole body before handing it to the proto parser. OTLP batches are
		// capped client-side (typical 512KB-2MB export window) so fully-buffered decode is
		// fine — protobuf has no streaming-parse affordance for repeated fields anyway.
		using var ms = new MemoryStream();
		await ctx.Request.Body.CopyToAsync(ms, ct);

		var result = OtlpLogsParser.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
		if (result.IsMalformed)
			return Results.BadRequest("malformed OTLP protobuf");

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(scope.Value, result.Candidates, ct);

		// OTLP spec says collectors respond 200 with ExportLogsServiceResponse. The standard
		// body is {"partialSuccess": {"rejectedLogRecords": N, "errorMessage": "..."}}} but
		// both otel-dotnet and otel-python accept any 2xx with empty/JSON body as "delivered".
		// We keep Created/202 semantics symmetric with CLEF rather than inventing a new shape.
		return Results.Created(ctx.Request.Path.Value, new IngestResponse(result.Candidates.Count, result.Errors));
	}

	static async Task<WorkspaceId?> ResolveScopeAsync(HttpContext ctx, IApiKeyStore apiKeys, CancellationToken ct)
	{
		var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
			?? ctx.Request.Query["apiKey"].FirstOrDefault();
		var validation = await apiKeys.ValidateAsync(token, ct);
		return validation.IsValid ? validation.Scope : null;
	}

	static void AccumulateResult(CleFLineResult line, List<LogEventCandidate> candidates, ref int errorCount)
	{
		if (line.IsSuccess && line.Event is not null)
			candidates.Add(line.Event);
		else
			errorCount++;
	}
}
