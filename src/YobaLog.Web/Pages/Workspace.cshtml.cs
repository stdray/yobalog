using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaLog.Core;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web.Pages;

public sealed class WorkspaceModel : PageModel
{
	readonly ILogStore _store;

	public WorkspaceModel(ILogStore store)
	{
		_store = store;
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

	public const int PageSize = 50;

	public List<LogEvent> Events { get; } = [];

	public string? NextCursor { get; private set; }

	public bool SchemaMissing { get; private set; }

	public string Kql { get; private set; } = "";

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();

		Workspace = ws;
		Kql = BuildKql();

		var code = KustoCode.Parse(Kql);

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

		if (Events.Count > PageSize)
		{
			var last = Events[PageSize - 1];
			Events.RemoveAt(Events.Count - 1);
			NextCursor = EncodeCursor(last);
		}

		return Page();
	}

	string BuildKql()
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
