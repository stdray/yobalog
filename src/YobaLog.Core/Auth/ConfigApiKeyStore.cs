using System.Collections.Immutable;
using Microsoft.Extensions.Options;

namespace YobaLog.Core.Auth;

public sealed class ConfigApiKeyStore : IApiKeyStore
{
    readonly ImmutableDictionary<string, (WorkspaceId? Workspace, bool CanCreate, int CreateWindowHours, string? Title)> _byToken;
    readonly ImmutableHashSet<WorkspaceId> _workspaces;

    public ConfigApiKeyStore(IOptions<ApiKeyOptions> options)
    {
        var byToken = ImmutableDictionary.CreateBuilder<string, (WorkspaceId?, bool, int, string?)>(StringComparer.Ordinal);
        var workspaces = ImmutableHashSet.CreateBuilder<WorkspaceId>();

        foreach (var key in options.Value.Keys)
        {
            if (string.IsNullOrEmpty(key.Token))
                continue;

            if (key.Workspace == "*")
            {
                byToken[key.Token] = (null, key.CanCreate, key.CreateWindowHours, key.Title);
                continue;
            }

            if (string.IsNullOrEmpty(key.Workspace))
                continue;
            if (!WorkspaceId.TryParse(key.Workspace, out var ws))
                continue;
            byToken[key.Token] = (ws, false, 0, key.Title);
            workspaces.Add(ws);
        }

        _byToken = byToken.ToImmutable();
        _workspaces = workspaces.ToImmutable();
    }

    public IReadOnlyCollection<WorkspaceId> ConfiguredWorkspaces => _workspaces;

    public ValueTask<ApiKeyValidation> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return ValueTask.FromResult(ApiKeyValidation.Invalid("missing api key"));
        if (!_byToken.TryGetValue(token, out var entry))
            return ValueTask.FromResult(ApiKeyValidation.Invalid("unknown api key"));

        if (entry.Workspace is { } ws)
            return ValueTask.FromResult(ApiKeyValidation.Valid(ws, entry.Title));

        var deadline = entry.CreateWindowHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-entry.CreateWindowHours)
            : (DateTimeOffset?)null;
        return ValueTask.FromResult(ApiKeyValidation.Wildcard(entry.CanCreate, null, entry.Title));
    }
}
