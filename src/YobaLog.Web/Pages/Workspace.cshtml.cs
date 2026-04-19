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

	public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
	{
		if (!WorkspaceId.TryParse(id, out var ws))
			return NotFound();

		Workspace = ws;

		var query = new LogQuery(
			PageSize: PageSize + 1,
			From: ParseDate(From),
			To: ParseDate(To),
			MinLevel: MinLevel,
			TraceId: NullIfEmpty(TraceId),
			MessageSubstring: NullIfEmpty(Message),
			Cursor: DecodeCursor(Cursor));

		try
		{
			await foreach (var e in _store.QueryAsync(ws, query, ct))
				Events.Add(e);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			// "no such table: Events" — workspace not yet created
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

	static DateTimeOffset? ParseDate(string? s) =>
		DateTimeOffset.TryParse(s, out var d) ? d : null;

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
