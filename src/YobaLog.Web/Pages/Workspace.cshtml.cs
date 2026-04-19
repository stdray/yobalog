using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Kql;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web.Pages;

public sealed class WorkspaceModel : PageModel
{
	readonly ILogStore _store;
	readonly ISavedQueryStore _savedQueries;

	public WorkspaceModel(ILogStore store, ISavedQueryStore savedQueries)
	{
		_store = store;
		_savedQueries = savedQueries;
	}

	public WorkspaceId Workspace { get; private set; }

	[BindProperty(SupportsGet = true)]
	public string? From { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? To { get; set; }

	[BindProperty(SupportsGet = true)]
	public LogLevel? MinLevel { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? TraceId { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? Message { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	[BindProperty(SupportsGet = true, Name = "saved")]
	public string? SavedName { get; set; }

	public const int PageSize = 50;

	public List<LogEvent> Events { get; } = [];

	public string? NextCursor { get; private set; }

	public bool SchemaMissing { get; private set; }

	public bool RawKqlMode { get; private set; }

	public string UserKql { get; private set; } = "";

	public string EffectiveKql { get; private set; } = "";

	public string? KqlError { get; private set; }

	public IReadOnlyList<SavedQuery> SavedQueries { get; private set; } = [];

	public string? ActiveSavedName { get; private set; }

	public string? FlashError { get; private set; }

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

		RawKqlMode = !string.IsNullOrWhiteSpace(RawKql);
		UserKql = RawKqlMode ? RawKql!.Trim() : BuildUserKql();
		EffectiveKql = AppendPageLimits(UserKql);

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
			await foreach (var e in _store.QueryKqlAsync(ws, code, ct))
				Events.Add(e);
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

		if (Events.Count > PageSize)
		{
			var last = Events[PageSize - 1];
			Events.RemoveAt(Events.Count - 1);
			NextCursor = EncodeCursor(last);
		}

		return Page();
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
			var tsLit = FormatDatetime(dt);
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Timestamp < {tsLit} or (Timestamp == {tsLit} and Id < {cid})");
		}
		sb.Append("\n| order by Timestamp desc, Id desc");
		sb.Append(CultureInfo.InvariantCulture, $"\n| take {PageSize + 1}");
		return sb.ToString();
	}

	string BuildUserKql()
	{
		var sb = new StringBuilder("LogEvents");

		if (TryParseUtc(From, out var fromDt))
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Timestamp >= {FormatDatetime(fromDt)}");
		if (TryParseUtc(To, out var toDt))
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Timestamp < {FormatDatetime(toDt)}");
		if (MinLevel is { } level)
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Level >= {(int)level}");
		if (NullIfEmpty(TraceId) is { } trace)
			sb.Append(CultureInfo.InvariantCulture, $"\n| where TraceId == '{EscapeKqlString(trace)}'");
		if (NullIfEmpty(Message) is { } msg)
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Message contains '{EscapeKqlString(msg)}'");

		return sb.ToString();
	}

	static bool TryParseUtc(string? s, out DateTimeOffset value) =>
		DateTimeOffset.TryParse(
			s,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out value);

	static string FormatDatetime(DateTimeOffset dt) =>
		$"datetime({dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)})";

	static string EscapeKqlString(string s) =>
		s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

	static string? NullIfEmpty(string? s) =>
		string.IsNullOrWhiteSpace(s) ? null : s;

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
