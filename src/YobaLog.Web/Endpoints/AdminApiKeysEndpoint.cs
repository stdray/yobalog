using Microsoft.AspNetCore.Mvc;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Endpoints;

// JSON admin-API for per-workspace API keys. Mirrors /ws/{id}/admin/api-keys Razor UI:
// PUT to create (plaintext returned exactly once), GET to list (no plaintext), DELETE to
// soft-delete. Auth — AdminTokenAuthFilter.
public static class AdminApiKeysEndpoint
{
    public static void MapAdminApiKeys(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPut("/workspaces/{ws}/api-keys", PutHandler);
        group.MapGet("/workspaces/{ws}/api-keys", ListHandler);
        group.MapDelete("/workspaces/{ws}/api-keys/{id}", DeleteHandler);
    }

    static async Task<IResult> PutHandler(
        string ws,
        [FromBody] ApiKeyCreateRequest? body,
        [FromServices] IWorkspaceStore workspaces,
        [FromServices] IApiKeyAdmin admin,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);
        if (await workspaces.GetAsync(workspace, ct).ConfigureAwait(false) is null)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var title = body?.Title;
        var created = await admin.CreateAsync(workspace, title, ct).ConfigureAwait(false);
        return Results.Json(new
        {
            id = created.Info.Id,
            prefix = created.Info.Prefix,
            plaintext = created.Plaintext,
            title = created.Info.Title,
            createdAt = created.Info.CreatedAt,
        }, statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> ListHandler(
        string ws,
        [FromServices] IWorkspaceStore workspaces,
        [FromServices] IApiKeyAdmin admin,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);
        if (await workspaces.GetAsync(workspace, ct).ConfigureAwait(false) is null)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var keys = await admin.ListAsync(workspace, ct).ConfigureAwait(false);
        var rows = keys.Select(k => new
        {
            id = k.Id,
            prefix = k.Prefix,
            title = k.Title,
            createdAt = k.CreatedAt,
        }).ToArray();
        return Results.Ok(rows);
    }

    static async Task<IResult> DeleteHandler(
        string ws,
        string id,
        [FromServices] IApiKeyAdmin admin,
        CancellationToken ct)
    {
        if (!WorkspaceId.TryParse(ws, out var workspace) || workspace.IsSystem)
            return Results.Json(new { error = "not_found", reason = $"workspace '{ws}' not found" },
                statusCode: StatusCodes.Status404NotFound);

        var deleted = await admin.DeleteAsync(workspace, id, ct).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : Results.Json(new { error = "not_found", reason = $"api-key '{id}' not found" },
                statusCode: StatusCodes.Status404NotFound);
    }
}

public sealed record ApiKeyCreateRequest(string? Title);
