using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core.Admin;

namespace YobaLog.Web.Pages;

public sealed class IndexModel : PageModel
{
    readonly IWorkspaceStore _workspaces;

    public IndexModel(IWorkspaceStore workspaces)
    {
        _workspaces = workspaces;
    }

    public ImmutableArray<WorkspaceInfo> Workspaces { get; private set; } = [];
    public ImmutableArray<IGrouping<string, WorkspaceInfo>> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var all = await _workspaces.ListAsync(HttpContext.RequestAborted);
        var user = all.Where(w => !w.Id.IsSystem).OrderBy(w => w.Id.Value, StringComparer.Ordinal).ToList();

        Groups = [.. user
            .GroupBy(w => string.IsNullOrEmpty(w.GroupName) ? "user" : w.GroupName)
            .OrderBy(g => g.Key == "user" ? "zzzzz" : g.Key)];

        Workspaces = [..user];
    }
}
