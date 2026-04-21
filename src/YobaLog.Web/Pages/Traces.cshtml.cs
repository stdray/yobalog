using System.Buffers.Binary;
using System.Collections.Immutable;
using Kusto.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Tracing;

namespace YobaLog.Web.Pages;

public sealed class TracesModel : PageModel
{
	readonly ISpanStore _spans;

	public TracesModel(ISpanStore spans)
	{
		_spans = spans;
	}

	public WorkspaceId Workspace { get; private set; }
	public ImmutableArray<TraceSummary> Traces { get; private set; } = [];
	public string? NextCursor { get; private set; }
	public string? KqlError { get; private set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? Kql { get; set; }

	// Poll cursor for incremental refresh — client-set via hx-vals from the topmost row's
	// `data-start-unix-ns`. Present only on htmx fragment requests; omitted on full page loads.
	[BindProperty(SupportsGet = true)]
	public long? Since { get; set; }

	public const int PageSize = 50;

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();
		Workspace = ws;

		KustoCode? filter = null;
		if (!string.IsNullOrWhiteSpace(Kql))
		{
			try
			{
				filter = KustoCode.Parse(Kql);
				var parseErrors = filter.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
				if (parseErrors.Count > 0)
				{
					KqlError = "KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message));
					filter = null;
				}
			}
			catch (Exception ex)
			{
				KqlError = ex.Message;
			}
		}

		var (cursorStart, cursorTraceId) = DecodeCursor(Cursor);

		// Two flows:
		//   Full page — Cursor pagination (page-by-page "Older →" navigation), no `since`.
		//   HX fragment — `since` set from the client's topmost row; return only newer traces.
		var query = Since is long sinceNs
			? new TracesQuery(PageSize: PageSize, SinceStartUnixNs: sinceNs, Filter: filter)
			: new TracesQuery(PageSize: PageSize + 1, CursorStartUnixNs: cursorStart, CursorTraceId: cursorTraceId, Filter: filter);

		IReadOnlyList<TraceSummary> page;
		try
		{
			page = await _spans.ListRecentTracesAsync(ws, query, ct);
		}
		catch (YobaLog.Core.Kql.UnsupportedKqlException ex)
		{
			KqlError = ex.Message;
			page = [];
		}

		if (Since is null && page.Count > PageSize)
		{
			var trimmed = page.Take(PageSize).ToList();
			Traces = [.. trimmed];
			var last = trimmed[^1];
			NextCursor = EncodeCursor(last);
		}
		else
		{
			Traces = [.. page];
		}

		// HX-Request header present on htmx-issued requests (including the every-5s poll) —
		// respond with just the row fragment so hx-swap="afterbegin" can prepend.
		if (Request.Headers.ContainsKey("HX-Request"))
			return Partial("_TracesRows", (IReadOnlyList<TraceSummary>)Traces);

		return Page();
	}

	// Cursor: base64url(StartUnixNs as 8 bytes BE | TraceId as raw bytes). Opaque to clients,
	// stable across yobalog versions because we control encode + decode in one place.
	static string EncodeCursor(TraceSummary last)
	{
		var startNs = last.StartTime.ToUnixTimeMilliseconds() * 1_000_000L;
		var traceIdBytes = System.Text.Encoding.ASCII.GetBytes(last.TraceId);
		var buf = new byte[8 + traceIdBytes.Length];
		BinaryPrimitives.WriteInt64BigEndian(buf, startNs);
		Buffer.BlockCopy(traceIdBytes, 0, buf, 8, traceIdBytes.Length);
		return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	static (long? startNs, string? traceId) DecodeCursor(string? cursor)
	{
		if (string.IsNullOrEmpty(cursor))
			return (null, null);
		try
		{
			var padded = cursor.Replace('-', '+').Replace('_', '/');
			padded += new string('=', (4 - padded.Length % 4) % 4);
			var buf = Convert.FromBase64String(padded);
			if (buf.Length < 8) return (null, null);
			var startNs = BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(0, 8));
			var traceId = System.Text.Encoding.ASCII.GetString(buf, 8, buf.Length - 8);
			return (startNs, traceId);
		}
		catch
		{
			return (null, null);
		}
	}
}
