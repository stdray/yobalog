using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages;

public sealed class IndexModel : PageModel
{
	readonly IApiKeyStore _apiKeys;

	public IndexModel(IApiKeyStore apiKeys)
	{
		_apiKeys = apiKeys;
	}

	public ImmutableArray<WorkspaceId> Workspaces { get; private set; } = [];

	public void OnGet()
	{
		Workspaces =
		[
			WorkspaceId.System,
			.. _apiKeys.ConfiguredWorkspaces.OrderBy(w => w.Value, StringComparer.Ordinal),
		];
	}
}
