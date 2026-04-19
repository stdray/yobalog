using System.Collections.Immutable;
using Microsoft.Extensions.Options;

namespace YobaLog.Core.Auth;

public sealed class ConfigApiKeyStore : IApiKeyStore
{
	readonly ImmutableDictionary<string, WorkspaceId> _byToken;
	readonly ImmutableHashSet<WorkspaceId> _workspaces;

	public ConfigApiKeyStore(IOptions<ApiKeyOptions> options)
	{
		var byToken = ImmutableDictionary.CreateBuilder<string, WorkspaceId>(StringComparer.Ordinal);
		var workspaces = ImmutableHashSet.CreateBuilder<WorkspaceId>();

		foreach (var key in options.Value.Keys)
		{
			if (string.IsNullOrEmpty(key.Token) || string.IsNullOrEmpty(key.Workspace))
				continue;
			if (!WorkspaceId.TryParse(key.Workspace, out var ws))
				continue;
			byToken[key.Token] = ws;
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
		return _byToken.TryGetValue(token, out var ws)
			? ValueTask.FromResult(ApiKeyValidation.Valid(ws))
			: ValueTask.FromResult(ApiKeyValidation.Invalid("unknown api key"));
	}
}
