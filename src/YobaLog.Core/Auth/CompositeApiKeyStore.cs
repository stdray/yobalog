namespace YobaLog.Core.Auth;

// Stacks multiple IApiKeyStore implementations, first-match-wins on validation.
// In the default wiring: ConfigApiKeyStore (appsettings, admin backdoor) + SqliteApiKeyStore
// (per-workspace DB, managed via UI). ConfiguredWorkspaces unions both so retention sweeps
// every workspace that has at least one key, regardless of source.
public sealed class CompositeApiKeyStore : IApiKeyStore
{
	readonly IReadOnlyList<IApiKeyStore> _stores;

	public CompositeApiKeyStore(params IApiKeyStore[] stores)
	{
		_stores = stores;
	}

	public async ValueTask<ApiKeyValidation> ValidateAsync(string? token, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(token))
			return ApiKeyValidation.Invalid("missing api key");

		foreach (var store in _stores)
		{
			var result = await store.ValidateAsync(token, ct).ConfigureAwait(false);
			if (result.IsValid)
				return result;
		}
		return ApiKeyValidation.Invalid("unknown api key");
	}

	public IReadOnlyCollection<WorkspaceId> ConfiguredWorkspaces
	{
		get
		{
			var set = new HashSet<WorkspaceId>();
			foreach (var store in _stores)
				foreach (var ws in store.ConfiguredWorkspaces)
					set.Add(ws);
			return set;
		}
	}
}
