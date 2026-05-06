using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kusto.Language;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Kql;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;

namespace YobaLog.Web.Pages;

public sealed class ShareKqlModel : PageModel
{
    readonly IKqlShareLinkStore _shareLinks;
    readonly IWorkspaceStore _workspaces;
    readonly ILogStore _store;

    public ShareKqlModel(IKqlShareLinkStore shareLinks, IWorkspaceStore workspaces, ILogStore store)
    {
        _shareLinks = shareLinks;
        _workspaces = workspaces;
        _store = store;
    }

    [FromRoute]
    public string Id { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? Kql { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Cursor { get; set; }

    public WorkspaceId Workspace { get; private set; }
    public string? WorkspaceDescription { get; private set; }
    public string? Agent { get; private set; }
    public string ShareUrl { get; private set; } = "";
    public string EffectiveKql { get; private set; } = "";
    public string UserKql { get; private set; } = "";
    public List<LogEvent> Events { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public bool IsExpired { get; private set; }
    public string? Error { get; private set; }
    public List<KqlRow> KqlRows { get; private set; } = [];
    public bool IsShapeChanged { get; private set; }

    public sealed record KqlRow(ImmutableArray<string> Columns, ImmutableArray<string?> Values);

    public async Task<IActionResult> OnGetAsync()
    {
        var link = await _shareLinks.GetAsync(Id, HttpContext.RequestAborted);
        if (link is null)
            return NotFound();

        if (link.ExpiresAt < DateTimeOffset.UtcNow)
        {
            IsExpired = true;
            return Page();
        }

        Workspace = link.Workspace;
        ShareUrl = $"{Request.Scheme}://{Request.Host}/share/kql/{link.Id}";

        var wsInfo = await _workspaces.GetAsync(link.Workspace, HttpContext.RequestAborted);
        WorkspaceDescription = wsInfo?.Description;
        Agent = wsInfo?.Agent;

        UserKql = string.IsNullOrWhiteSpace(Kql) ? link.Kql : Kql;
        EffectiveKql = UserKql.Trim();

        KustoCode code;
        try
        {
            code = KustoCode.Parse(EffectiveKql);
            var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
            if (errors.Count > 0)
            {
                Error = "KQL parse error: " + string.Join("; ", errors.Select(e => e.Message));
                return Page();
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return Page();
        }

        try
        {
            IsShapeChanged = KqlTransformer.HasShapeChangingOps(code);
            if (IsShapeChanged)
            {
                var result = await _store.QueryKqlResultAsync(link.Workspace, code, HttpContext.RequestAborted);
                await foreach (var row in result.Rows)
                {
                    var values = row.Select(v => v?.ToString()).ToImmutableArray();
                    var columns = result.Columns.Select(c => c.Name).ToImmutableArray();
                    KqlRows.Add(new KqlRow(columns, values));
                }
            }
            else
            {
                var pagedKql = AppendPageLimits(EffectiveKql, Cursor);
                var pagedCode = KustoCode.Parse(pagedKql);

                await foreach (var e in _store.QueryKqlAsync(link.Workspace, pagedCode, HttpContext.RequestAborted))
                    Events.Add(e);

                if (Events.Count > PageSize)
                {
                    var last = Events[^2]; // last before the extra one
                    Events.RemoveAt(Events.Count - 1);
                    NextCursor = EncodeCursor(last.Timestamp.ToUnixTimeMilliseconds(), last.Id);
                }
            }
        }
        catch (UnsupportedKqlException ex)
        {
            Error = ex.Message;
            return Page();
        }

        if (Request.Headers.TryGetValue("HX-Request", out _))
            return Partial("_RowsFragment", this);

        return Page();
    }

    const int PageSize = 50;

    internal static string AppendPageLimits(string kql, string? cursor)
    {
        var sb = new System.Text.StringBuilder(kql.TrimEnd());
        sb.Append("\n| order by Timestamp desc, Id desc");
        sb.Append("\n| take ");
        sb.Append(PageSize + 1);

        if (!string.IsNullOrEmpty(cursor))
        {
            var (ts, id) = DecodeCursor(cursor);
            sb.Append("\n| where Timestamp < datetime(");
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(") or (Timestamp == datetime(");
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(") and Id < ");
            sb.Append(id);
            sb.Append(')');
        }

        return sb.ToString();
    }

    static string EncodeCursor(long ts, long id)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, ts);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes[8..], id);
        return Convert.ToBase64String(bytes);
    }

    static (long ts, long id) DecodeCursor(string cursor)
    {
        var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/'));
        var padded = new byte[16];
        bytes.CopyTo(padded.AsSpan());
        var ts = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(padded);
        var id = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(padded.AsSpan(8));
        return (ts, id);
    }
}
