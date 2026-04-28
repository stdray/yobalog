using Microsoft.AspNetCore.Mvc;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;

namespace YobaLog.Web.Endpoints;

// JSON admin-API for retention policies. Mirrors /admin/retention Razor UI: GET to list
// the workspace's policies, PUT to upsert a (savedQuery, retainDays) rule, DELETE to drop
// one. Retention in yobalog is per-(workspace, savedQuery), not a single retainDays per
// workspace — see doc/admin-api.md for the rationale and per-workspace summary.
public static class AdminRetentionEndpoint
{
    public static void MapAdminRetention(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/workspaces/{ws}/retention", ListHandler);
        group.MapPut("/workspaces/{ws}/retention", PutHandler);
        group.MapDelete("/workspaces/{ws}/retention/{savedQuery}", DeleteHandler);
    }

    static async Task<IResult> ListHandler(
        string ws,
        [FromServices] IWorkspaceStore workspaces,
        [FromServices] IRetentionPolicyStore store,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);
        if (await workspaces.GetAsync(workspace, ct).ConfigureAwait(false) is null)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var rows = await store.ListByWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        return Results.Ok(rows
            .Select(p => new { savedQuery = p.SavedQuery, retainDays = p.RetainDays })
            .ToArray());
    }

    static async Task<IResult> PutHandler(
        string ws,
        [FromBody] RetentionUpsertRequest? body,
        [FromServices] IWorkspaceStore workspaces,
        [FromServices] ISavedQueryStore savedQueries,
        [FromServices] IRetentionPolicyStore store,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);
        if (await workspaces.GetAsync(workspace, ct).ConfigureAwait(false) is null)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        if (body is null || string.IsNullOrWhiteSpace(body.SavedQuery))
            return Results.Json(new { error = "bad_request", reason = "savedQuery is required" },
                statusCode: StatusCodes.Status400BadRequest);
        if (body.RetainDays <= 0)
            return Results.Json(new { error = "bad_request", reason = "retainDays must be a positive integer" },
                statusCode: StatusCodes.Status400BadRequest);

        var saved = await savedQueries.GetByNameAsync(workspace, body.SavedQuery, ct).ConfigureAwait(false);
        if (saved is null)
            return Results.Json(new
            {
                error = "not_found",
                reason = $"saved query '{body.SavedQuery}' not found in workspace '{workspace.Value}'",
            }, statusCode: StatusCodes.Status404NotFound);

        await store.UpsertAsync(
            new RetentionPolicy
            {
                Workspace = workspace.Value,
                SavedQuery = body.SavedQuery,
                RetainDays = body.RetainDays,
            }, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            savedQuery = body.SavedQuery,
            retainDays = body.RetainDays,
        });
    }

    static async Task<IResult> DeleteHandler(
        string ws,
        string savedQuery,
        [FromServices] IRetentionPolicyStore store,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var deleted = await store.DeleteAsync(workspace, savedQuery, ct).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : Results.Json(new
            {
                error = "not_found",
                reason = $"policy for '{workspace.Value}' / '{savedQuery}' not found",
            }, statusCode: StatusCodes.Status404NotFound);
    }
}

public sealed record RetentionUpsertRequest(string? SavedQuery, int RetainDays);
