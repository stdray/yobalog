using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Kql;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Sharing;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Web.Pages;

public sealed class WorkspaceModel : PageModel
{
	readonly ILogStore _store;
	readonly ISavedQueryStore _savedQueries;
	readonly IFieldMaskingPolicyStore _maskingPolicies;

	public WorkspaceModel(
		ILogStore store,
		ISavedQueryStore savedQueries,
		IFieldMaskingPolicyStore maskingPolicies)
	{
		_store = store;
		_savedQueries = savedQueries;
		_maskingPolicies = maskingPolicies;
	}

	public WorkspaceId Workspace { get; private set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	[BindProperty(SupportsGet = true, Name = "saved")]
	public string? SavedName { get; set; }

	public const int PageSize = 50;

	public List<LogEvent> Events { get; } = [];

	public KqlResult? KqlResult { get; private set; }

	public List<object?[]> KqlRows { get; } = [];

	public bool IsShapeChanged { get; private set; }

	public string? NextCursor { get; private set; }

	public bool SchemaMissing { get; private set; }

	public string UserKql { get; private set; } = "";

	public string EffectiveKql { get; private set; } = "";

	public string? KqlError { get; private set; }

	public IReadOnlyList<SavedQuery> SavedQueries { get; private set; } = [];

	public string? ActiveSavedName { get; private set; }

	public string? FlashError { get; private set; }

	public FieldMaskingPolicy MaskingPolicy { get; private set; } = FieldMaskingPolicy.Empty;

	public IReadOnlyList<string> ShareFieldPaths { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();

		Workspace = ws;
		SavedQueries = await _savedQueries.ListAsync(ws, ct);

		if (!string.IsNullOrWhiteSpace(SavedName))
		{
			var saved = await _savedQueries.GetByNameAsync(ws, SavedName, ct);
			if (saved is not null)
			{
				RawKql = saved.Kql;
				ActiveSavedName = saved.Name;
			}
		}

		UserKql = string.IsNullOrWhiteSpace(RawKql) ? "events" : RawKql.Trim();

		// Detect shape-changing ops on the user's KQL before appending paging / cap — our own
		// `order by Timestamp` doesn't make sense after `count` / `summarize`.
		KustoCode userCode;
		try
		{
			userCode = KustoCode.Parse(UserKql);
			var parseErrors = userCode.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
			if (parseErrors.Count > 0)
			{
				KqlError = "KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message));
				return Page();
			}
		}
		catch (Exception ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		IsShapeChanged = KqlTransformer.HasShapeChangingOps(userCode);
		// Shape-changed queries keep the user's KQL intact — our pager (order by Timestamp + take) doesn't
		// compose cleanly after shape-changing ops, and take in post-shape position isn't supported yet.
		// The store caps materialized rows to prevent runaway memory on unbounded 'project' / 'extend'.
		EffectiveKql = IsShapeChanged ? UserKql : AppendPageLimits(UserKql);

		KustoCode code;
		try
		{
			code = KustoCode.Parse(EffectiveKql);
			var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
			if (errors.Count > 0)
			{
				KqlError = "KQL parse error: " + string.Join("; ", errors.Select(d => d.Message));
				return Page();
			}
		}
		catch (Exception ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		try
		{
			if (IsShapeChanged)
			{
				KqlResult = await _store.QueryKqlResultAsync(ws, code, ct);
				await foreach (var row in KqlResult.Rows.WithCancellation(ct))
					KqlRows.Add(row);
			}
			else
			{
				await foreach (var e in _store.QueryKqlAsync(ws, code, ct))
					Events.Add(e);
			}
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			SchemaMissing = true;
			return Page();
		}
		catch (UnsupportedKqlException ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		if (!IsShapeChanged && Events.Count > PageSize)
		{
			var last = Events[PageSize - 1];
			Events.RemoveAt(Events.Count - 1);
			NextCursor = EncodeCursor(last);
		}

		if (Request.Headers.ContainsKey("HX-Request") && !IsShapeChanged)
			return Partial("_RowsFragment", this);

		MaskingPolicy = await _maskingPolicies.GetAsync(ws, ct);
		ShareFieldPaths = BuildShareFieldPaths();

		return Page();
	}

	List<string> BuildShareFieldPaths()
	{
		// Order mirrors the event row. Flat namespace: top-level scalars and property keys coexist —
		// users don't distinguish them, and TsvExporter resolves collisions by shadowing property keys
		// with top-level scalars.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<string>();
		void Add(string p)
		{
			if (seen.Add(p))
				result.Add(p);
		}

		Add(nameof(LogEvent.Message));
		Add(nameof(LogEvent.MessageTemplate));
		Add(nameof(LogEvent.Exception));
		Add(nameof(LogEvent.TraceId));
		Add(nameof(LogEvent.SpanId));
		Add(nameof(LogEvent.EventId));

		var propKeys = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var e in Events)
			foreach (var key in e.Properties.Keys)
				propKeys.Add(key);
		foreach (var key in propKeys)
			Add(key);

		// Paths already in policy but missing from the current sample — keep visible.
		foreach (var key in MaskingPolicy.Modes.Keys.OrderBy(k => k, StringComparer.Ordinal))
			Add(key);

		return result;
	}

	public async Task<IActionResult> OnPostSaveAsync(
		string id,
		[FromForm(Name = "name")] string? name,
		[FromForm(Name = "kql")] string? kql,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();

		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kql))
			return RedirectToPage(new { id });

		await _savedQueries.UpsertAsync(ws, name.Trim(), kql, ct);
		return RedirectToPage(new { id, saved = name.Trim() });
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		string id,
		[FromForm(Name = "savedId")] long savedId,
		CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();

		await _savedQueries.DeleteAsync(ws, savedId, ct);
		return RedirectToPage(new { id });
	}

	string AppendPageLimits(string userKql)
	{
		var sb = new StringBuilder(userKql.TrimEnd());
		if (DecodeCursor(Cursor) is { } cursorBytes)
		{
			var (ts, cid) = CursorCodec.Decode(cursorBytes.Span);
			var dt = DateTimeOffset.FromUnixTimeMilliseconds(ts);
			var tsLit = $"datetime({dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)})";
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Timestamp < {tsLit} or (Timestamp == {tsLit} and Id < {cid})");
		}
		sb.Append("\n| order by Timestamp desc, Id desc");
		sb.Append(CultureInfo.InvariantCulture, $"\n| take {PageSize + 1}");
		return sb.ToString();
	}

	static ReadOnlyMemory<byte>? DecodeCursor(string? s)
	{
		if (string.IsNullOrEmpty(s))
			return null;
		try
		{
			return Convert.FromBase64String(s);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	static string EncodeCursor(LogEvent last)
	{
		var bytes = CursorCodec.Encode(last.Timestamp.ToUnixTimeMilliseconds(), last.Id);
		return Convert.ToBase64String(bytes.Span);
	}
}
