using Microsoft.AspNetCore.Mvc;
using YobaLog.Core;
using YobaLog.Core.Admin;

namespace YobaLog.Web.Endpoints;

// JSON admin-API for the workspace catalog. Mirrors /admin/workspaces Razor UI:
// PUT to create-if-absent (idempotent), GET to list/single, DELETE to drop. Auth —
// AdminTokenAuthFilter.
public static class AdminWorkspacesEndpoint
{
    public static void MapAdminWorkspaces(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPut("/workspaces", PutHandler);
        group.MapGet("/workspaces", ListHandler);
        group.MapGet("/workspaces/{id}", GetHandler);
        group.MapDelete("/workspaces/{id}", DeleteHandler);
    }

    static async Task<IResult> PutHandler(
        [FromBody] WorkspaceCreateRequest? body,
        [FromServices] IWorkspaceStore store,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
            return Results.Json(new { error = "bad_request", reason = "id is required" },
                statusCode: StatusCodes.Status400BadRequest);

        if (!WorkspaceId.TryParse(body.Id, out var ws) || ws.IsSystem)
            return Results.Json(new
            {
                error = "bad_request",
                reason = "id must match ^[a-z0-9][a-z0-9-]{1,39}$ (no $-prefix; that's reserved).",
            }, statusCode: StatusCodes.Status400BadRequest);

        var existing = await store.GetAsync(ws, ct).ConfigureAwait(false);
        if (existing is not null)
            return Results.Json(new { id = existing.Id.Value, createdAt = existing.CreatedAt },
                statusCode: StatusCodes.Status200OK);

        var created = await store.CreateAsync(ws, ct).ConfigureAwait(false);
        return Results.Json(new { id = created.Id.Value, createdAt = created.CreatedAt },
            statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> ListHandler(
        [FromServices] IWorkspaceStore store,
        CancellationToken ct)
    {
        var all = await store.ListAsync(ct).ConfigureAwait(false);
        var rows = all.Where(w => !w.Id.IsSystem)
            .Select(w => new { id = w.Id.Value, createdAt = w.CreatedAt })
            .ToArray();
        return Results.Ok(rows);
    }

    static async Task<IResult> GetHandler(
        string id,
        [FromServices] IWorkspaceStore store,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(id, out var ws) || ws.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{id}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var info = await store.GetAsync(ws, ct).ConfigureAwait(false);
        return info is null
            ? Results.Json(new { error = "not_found", reason = $"workspace '{id}' not found" },
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new { id = info.Id.Value, createdAt = info.CreatedAt });
    }

    static async Task<IResult> DeleteHandler(
        string id,
        [FromServices] IWorkspaceStore store,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(id, out var ws) || ws.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{id}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var deleted = await store.DeleteAsync(ws, ct).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : Results.Json(new { error = "not_found", reason = $"workspace '{id}' not found" },
                statusCode: StatusCodes.Status404NotFound);
    }
}

public sealed record WorkspaceCreateRequest(string? Id);
