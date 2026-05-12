using System.Collections.Immutable;
using System.Text.Json;
using Kusto.Language;
using YobaLog.Core;
using YobaLog.Core.Auth;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage;

namespace YobaLog.Web;

static class QueryHandlers
{
    const int DefaultPageSize = 50;
    static readonly JsonSerializerOptions PostJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IResult> GetAsync(
        HttpContext ctx,
        IApiKeyStore apiKeys,
        ILogStore store,
        CancellationToken ct)
    {
        var workspace = ctx.Request.RouteValues["ws"] as string;
        var kql = ctx.Request.Query["kql"].FirstOrDefault();
        var cursor = ctx.Request.Query["cursor"].FirstOrDefault();
        return await HandleAsync(ctx, apiKeys, store, workspace, kql, cursor, ct);
    }

    public static async Task<IResult> PostAsync(
        HttpContext ctx,
        IApiKeyStore apiKeys,
        ILogStore store,
        CancellationToken ct)
    {
        var body = await JsonSerializer.DeserializeAsync<QueryRequest>(
            ctx.Request.Body, PostJsonOptions, ct);
        if (body is null)
            return Results.BadRequest("invalid JSON body");
        var workspace = ctx.Request.RouteValues["ws"] as string ?? body.Workspace;
        return await HandleAsync(ctx, apiKeys, store, workspace, body.Kql, body.Cursor, ct);
    }

    static async Task<IResult> HandleAsync(
        HttpContext ctx,
        IApiKeyStore apiKeys,
        ILogStore store,
        string? workspace,
        string? kql,
        string? cursor,
        CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
            ?? ctx.Request.Query["apiKey"].FirstOrDefault();
        var validation = await apiKeys.ValidateAsync(token, ct);
        if (!validation.IsValid)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(workspace))
            return Results.BadRequest("workspace required");
        if (!WorkspaceId.TryParse(workspace, out var ws))
            return Results.BadRequest($"invalid workspace: {workspace}");
        if (string.IsNullOrWhiteSpace(kql))
            return Results.BadRequest("kql required");

        if (!validation.IsWildcard && validation.Scope != ws)
            return Results.Unauthorized();

        KustoCode code;
        try
        {
            code = KustoCode.Parse(kql);
            var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
            if (errors.Count > 0)
                return Results.BadRequest("KQL parse error: " + string.Join("; ", errors.Select(e => e.Message)));
        }
        catch (Exception ex)
        {
            return Results.BadRequest("KQL parse error: " + ex.Message);
        }

        try
        {
            if (KqlTransformer.HasShapeChangingOps(code))
                return await ExecuteShapeChanging(store, ws, code, ct);

            return await ExecuteEventShaped(store, ws, kql, cursor, ct);
        }
        catch (UnsupportedKqlException ex)
        {
            return Results.BadRequest("unsupported KQL: " + ex.Message);
        }
    }

    static async Task<IResult> ExecuteShapeChanging(ILogStore store, WorkspaceId ws, KustoCode code, CancellationToken ct)
    {
        var result = await store.QueryKqlResultAsync(ws, code, ct);
        var rows = new List<ImmutableArray<JsonElement?>>();
        await foreach (var row in result.Rows.WithCancellation(ct))
        {
            var arr = ImmutableArray.CreateRange(row.Select(cell =>
                cell is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(cell)));
            rows.Add(arr);
        }
        var columns = ImmutableArray.CreateRange(result.Columns.Select(c => c.Name));
        return Results.Json(new QueryResponse(columns, [.. rows], null, false));
    }

    static async Task<IResult> ExecuteEventShaped(
        ILogStore store, WorkspaceId ws, string kql, string? cursor, CancellationToken ct)
    {
        var pagedKql = AppendPaging(kql, cursor);
        var pagedCode = KustoCode.Parse(pagedKql);

        var events = new List<LogEvent>();
        await foreach (var e in store.QueryKqlAsync(ws, pagedCode, ct))
            events.Add(e);

        var truncated = events.Count > DefaultPageSize;
        if (truncated)
            events.RemoveAt(events.Count - 1);

        string? nextCursor = null;
        if (truncated && events.Count > 0)
        {
            var last = events[^1];
            nextCursor = EncodeCursor(last.Timestamp.ToUnixTimeMilliseconds(), last.Id);
        }

        var eventRows = ImmutableArray.CreateRange(events.Select(EventToRow));
        var eventColumns = ImmutableArray.Create("Timestamp", "Level", "LevelName", "Message",
            "MessageTemplate", "Exception", "TraceId", "SpanId", "EventId", "Properties");

        return Results.Json(new QueryResponse(eventColumns, eventRows, nextCursor, truncated));
    }

    static string AppendPaging(string kql, string? cursor)
    {
        var sb = new System.Text.StringBuilder(kql.TrimEnd());
        sb.Append("\n| order by Timestamp desc, Id desc");

        if (!string.IsNullOrEmpty(cursor))
        {
            var (ts, id) = DecodeCursor(cursor);
            sb.Append("\n| where Timestamp < datetime(");
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(") or (Timestamp == datetime(");
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(") and Id < ");
            sb.Append(id);
            sb.Append(')');
        }

        sb.Append("\n| take ");
        sb.Append(DefaultPageSize + 1);
        return sb.ToString();
    }

    static ImmutableArray<JsonElement?> EventToRow(LogEvent e) =>
    [
        JsonSerializer.SerializeToElement(e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)),
        JsonSerializer.SerializeToElement(e.Level.ToString()),
        JsonSerializer.SerializeToElement(LogLevelName(e.Level)),
        JsonSerializer.SerializeToElement(e.Message),
        JsonSerializer.SerializeToElement(e.MessageTemplate),
        e.Exception is not null ? JsonSerializer.SerializeToElement(e.Exception) : null,
        e.TraceId is not null ? JsonSerializer.SerializeToElement(e.TraceId) : null,
        e.SpanId is not null ? JsonSerializer.SerializeToElement(e.SpanId) : null,
        e.EventId is not null ? JsonSerializer.SerializeToElement(e.EventId.Value) : null,
        e.Properties.Count > 0 ? PropertiesToElement(e.Properties) : null,
    ];

    static JsonElement PropertiesToElement(ImmutableDictionary<string, JsonElement> props)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var (k, v) in props)
        {
            writer.WritePropertyName(k);
            v.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static string LogLevelName(Core.LogLevel l) => l switch
    {
        Core.LogLevel.Verbose => "Verbose",
        Core.LogLevel.Debug => "Debug",
        Core.LogLevel.Information => "Information",
        Core.LogLevel.Warning => "Warning",
        Core.LogLevel.Error => "Error",
        Core.LogLevel.Fatal => "Fatal",
        _ => "Unknown",
    };

    static string EncodeCursor(long timestampMs, long id)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, timestampMs);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes[8..], id);
        return Convert.ToBase64String(bytes);
    }

    static (long ts, long id) DecodeCursor(string cursor)
    {
        var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/'));
        var padded = new byte[16];
        bytes.CopyTo(padded.AsSpan());
        var ts = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(padded);
        var id = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(padded.AsSpan(8));
        return (ts, id);
    }
}