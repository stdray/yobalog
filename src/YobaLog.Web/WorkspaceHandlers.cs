using System.Text.Json;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Auth;

namespace YobaLog.Web;

static class WorkspaceHandlers
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IResult> UpsertAsync(
        HttpContext ctx,
        string ws,
        IApiKeyStore apiKeys,
        IWorkspaceStore workspaces,
        CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
            ?? ctx.Request.Query["apiKey"].FirstOrDefault();
        var validation = await apiKeys.ValidateAsync(token, ct);
        if (!validation.IsValid)
            return Results.Unauthorized();

        if (!WorkspaceId.TryParse(ws, out var workspaceId))
            return Results.BadRequest($"invalid workspace name: {ws}");

        if (!validation.IsWildcard && validation.Scope != workspaceId)
            return Results.Unauthorized();

        // Check if already exists — idempotent PUT.
        var existing = await workspaces.GetAsync(workspaceId, ct);
        if (existing is not null)
            return Results.Ok(existing);

        // Only wildcard keys with CanCreate + active window can create.
        if (!validation.IsWildcard || !validation.CanCreate)
            return Results.Json("key cannot create workspaces", statusCode: StatusCodes.Status403Forbidden);

        if (validation.CreateDeadline is { } deadline && DateTimeOffset.UtcNow > deadline)
            return Results.Json("creation window expired", statusCode: StatusCodes.Status403Forbidden);

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var groupName = root.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
        var agent = validation.Title ?? "unknown";

        var info = await workspaces.CreateAsync(workspaceId, description, agent, groupName, ct);
        return Results.Json(info, statusCode: StatusCodes.Status201Created);
    }

    public static async Task<IResult> GetInfoAsync(
        HttpContext ctx,
        string ws,
        IApiKeyStore apiKeys,
        IWorkspaceStore workspaces,
        CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
            ?? ctx.Request.Query["apiKey"].FirstOrDefault();
        var validation = await apiKeys.ValidateAsync(token, ct);
        if (!validation.IsValid)
            return Results.Unauthorized();

        if (!WorkspaceId.TryParse(ws, out var workspaceId))
            return Results.BadRequest($"invalid workspace name: {ws}");

        if (!validation.IsWildcard && validation.Scope != workspaceId)
            return Results.Unauthorized();

        var info = await workspaces.GetAsync(workspaceId, ct);
        return info is null ? Results.NotFound() : Results.Ok(info);
    }
}
