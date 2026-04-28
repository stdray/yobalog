using System.Security.Claims;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Endpoints;

// Authentication for /v1/admin/* — personal admin tokens. Three equivalent transports:
//   - Authorization: Bearer <token>   (HTTP standard, generic-tooling-friendly)
//   - X-YobaLog-AdminToken: <token>   (mirrors X-Seq-ApiKey naming)
//   - ?adminToken=<token>             (curl-quick-test fallback; lowest precedence)
//
// If both Authorization+Bearer AND X-YobaLog-AdminToken arrive with different values, the
// request is rejected with 400 ambiguous_auth — refuse to guess which one the caller meant.
// Same value in both is fine. Any header wins over query unconditionally (no conflict possible
// across header/query since query is a fallback channel).
//
// On success the resolved AdminToken is cached in HttpContext.Items[ItemKey] for handler
// access, and HttpContext.User is set to a ClaimsPrincipal with the token's Username so
// `User.Identity?.Name` works the same as cookie-auth pages.
public static class AdminTokenAuth
{
    public const string ItemKey = "yobalog.admin-token";
    public const string AuthenticationType = "AdminToken";
    public const string HeaderName = "X-YobaLog-AdminToken";
    public const string QueryParam = "adminToken";

    public static RouteGroupBuilder RequireAdminToken(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.AddEndpointFilter<AdminTokenAuthFilter>();
        // /v1/admin/* bypasses the app-wide cookie-auth fallback policy; the filter is the
        // sole gate. AllowAnonymous tells the Authorization middleware to skip the fallback.
        group.WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
        return group;
    }

    public static AdminToken? CurrentToken(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var v) ? v as AdminToken : null;
}

public sealed class AdminTokenAuthFilter : IEndpointFilter
{
    readonly IAdminTokenStore _store;

    public AdminTokenAuthFilter(IAdminTokenStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;

        var bearer = ExtractBearer(http);
        var customHeader = http.Request.Headers[AdminTokenAuth.HeaderName].FirstOrDefault();
        var query = http.Request.Query[AdminTokenAuth.QueryParam].FirstOrDefault();

        // Same-value-in-both is fine; differing values across the two header channels is
        // an unrecoverable ambiguity — the caller has wired conflicting auth sources and we
        // won't pick one silently. Query is fallback only and doesn't participate in the
        // conflict check.
        if (!string.IsNullOrEmpty(bearer) && !string.IsNullOrEmpty(customHeader) &&
            !string.Equals(bearer, customHeader, StringComparison.Ordinal))
        {
            return Results.Json(new
            {
                error = "ambiguous_auth",
                reason = $"Authorization: Bearer and {AdminTokenAuth.HeaderName} disagree. Send one or matching values.",
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        var token = !string.IsNullOrEmpty(bearer) ? bearer
            : !string.IsNullOrEmpty(customHeader) ? customHeader
            : query;

        var validation = await _store.ValidateAsync(token, http.RequestAborted).ConfigureAwait(false);
        if (validation is AdminTokenValidation.Invalid invalid)
        {
            return Results.Json(new
            {
                error = "unauthorized",
                reason = invalid.Reason,
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var valid = (AdminTokenValidation.Valid)validation;
        http.Items[AdminTokenAuth.ItemKey] = valid.Token;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, valid.Token.Username)],
            AdminTokenAuth.AuthenticationType));

        return await next(context);
    }

    static string? ExtractBearer(HttpContext http)
    {
        var raw = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(raw)) return null;
        const string prefix = "Bearer ";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..].Trim()
            : null;
    }
}
