using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Auth;

namespace YobaLog.Web.Pages.Admin;

public sealed class AgentKeyModel : PageModel
{
    readonly IApiKeyAdmin _admin;
    readonly IWebHostEnvironment _env;
    readonly IRazorPartialRenderer _renderer;

    public AgentKeyModel(IApiKeyAdmin admin, IWebHostEnvironment env, IRazorPartialRenderer renderer)
    {
        _admin = admin;
        _env = env;
        _renderer = renderer;
    }

    [BindProperty]
    public string Title { get; set; } = "";

    [BindProperty]
    public int CreateWindowHours { get; set; } = 4;

    public string? AgentToken { get; private set; }
    public string? AgentUrl { get; private set; }
    public string WindowText { get; private set; } = "";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Title))
            Title = "agent";

        var created = await _admin.CreateAsync(
            WorkspaceId.System, Title.Trim(), ct,
            isWildcard: true, canCreate: true, createWindowHours: CreateWindowHours);

        AgentToken = created.Plaintext;
        WindowText = CreateWindowHours == 1 ? "1 hour" : $"{CreateWindowHours} hours";

        var instructions = await _renderer.RenderAsync("_AgentInstructions", this, HttpContext);

        var dir = Path.Combine(_env.WebRootPath, "agent");
        Directory.CreateDirectory(dir);

        var fileId = ShortGuid.NewShortGuid().ToString();
        var filePath = Path.Combine(dir, fileId + ".md");
        await System.IO.File.WriteAllTextAsync(filePath, instructions, ct);

        AgentUrl = $"{Request.Scheme}://{Request.Host}/agent/{fileId}.md";

        _ = Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None)
            .ContinueWith(_ =>
            {
                try { System.IO.File.Delete(filePath); } catch { }
            }, TaskScheduler.Default);

        return Page();
    }
}
