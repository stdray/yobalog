using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YobaLog.Core;
using YobaLog.Core.Auth;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;
using YobaLog.Web.Pages;
using Kusto.Language;

namespace YobaLog.Web;

static class ShareHandlers
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    public static async Task<IResult> CreateAsync(
        HttpContext ctx,
        IApiKeyStore apiKeys,
        ILogStore store,
        IKqlShareLinkStore shareLinks,
        CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
            ?? ctx.Request.Query["apiKey"].FirstOrDefault();
        var validation = await apiKeys.ValidateAsync(token, ct);
        if (!validation.IsValid)
            return Results.Unauthorized();

        var body = await JsonSerializer.DeserializeAsync<ShareCreateRequest>(
            ctx.Request.Body, JsonOpts, ct);
        if (body is null)
            return Results.BadRequest("invalid JSON body");

        if (string.IsNullOrWhiteSpace(body.Workspace))
            return Results.BadRequest("workspace required");
        if (!WorkspaceId.TryParse(body.Workspace, out var ws))
            return Results.BadRequest($"invalid workspace: {body.Workspace}");
        if (string.IsNullOrWhiteSpace(body.Kql))
            return Results.BadRequest("kql required");

        if (!validation.IsWildcard && validation.Scope != ws)
            return Results.Unauthorized();

        var ttlHours = body.TtlHours is > 0 ? body.TtlHours.Value : 24;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(ttlHours);

        // Verify KQL parses.
        try
        {
            var code = KustoCode.Parse(body.Kql);
            var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
            if (errors.Count > 0)
                return Results.BadRequest("KQL parse error: " + string.Join("; ", errors.Select(e => e.Message)));
        }
        catch (Exception ex)
        {
            return Results.BadRequest("KQL parse error: " + ex.Message);
        }

        var link = await shareLinks.CreateAsync(ws, body.Kql, expiresAt, ct);

        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/share/kql/{link.Id}";
        return Results.Ok(new ShareCreateResponse(url, expiresAt));
    }

    public static async Task<IResult> RowsFragmentAsync(
        string id,
        HttpContext ctx,
        ILogStore store,
        IKqlShareLinkStore shareLinks,
        CancellationToken ct)
    {
        var link = await shareLinks.GetAsync(id, ct);
        if (link is null)
            return Results.NotFound();
        if (link.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.StatusCode(StatusCodes.Status410Gone);

        var kql = ctx.Request.Query["kql"].FirstOrDefault() ?? link.Kql;
        var cursor = ctx.Request.Query["cursor"].FirstOrDefault();

        var pagedKql = ShareKqlModel.AppendPageLimits(kql, cursor);
        KustoCode code;
        try
        {
            code = KustoCode.Parse(pagedKql);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(ex.Message);
        }

        var events = new List<LogEvent>();
        await foreach (var e in store.QueryKqlAsync(link.Workspace, code, ct))
            events.Add(e);

        var truncated = events.Count > 50;
        if (truncated)
            events.RemoveAt(events.Count - 1);

        string? nextCursor = null;
        if (truncated && events.Count > 0)
        {
            var last = events[^1];
            Span<byte> bytes = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, last.Timestamp.ToUnixTimeMilliseconds());
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes[8..], last.Id);
            nextCursor = Convert.ToBase64String(bytes);
        }

        // Render rows as HTML fragment.
        var sb = new System.Text.StringBuilder();
        foreach (var e in events)
        {
            var vm = EventRowViewModel.FromStored(e);
            var html = await InnerRenderAsync(ctx, "_EventRow", vm);
            sb.Append(html);
        }

        if (nextCursor is not null)
        {
            var nextUrl = $"/share/kql/{id}/rows?kql={Uri.EscapeDataString(kql)}&cursor={Uri.EscapeDataString(nextCursor)}";
            sb.Append($"<tr data-testid=\"events-sentinel\" hx-get=\"{nextUrl}\" hx-trigger=\"intersect once\" hx-swap=\"outerHTML\" hx-target=\"this\"><td colspan=\"4\" class=\"text-center text-xs opacity-50 py-4\"><span class=\"loading loading-spinner loading-xs\"></span> Loading older…</td></tr>");
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(sb.ToString(), ct);
        return Results.Empty;
    }

    static async Task<string> InnerRenderAsync(HttpContext ctx, string partialName, object model)
    {
        var renderer = ctx.RequestServices.GetRequiredService<IRazorPartialRenderer>();
        return await renderer.RenderAsync(partialName, model, ctx);
    }

    sealed record ShareCreateRequest(string Workspace, string Kql, int? TtlHours);
    sealed record ShareCreateResponse(string Url, DateTimeOffset ExpiresAt);
}
