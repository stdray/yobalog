using Kusto.Language.Editor;

namespace YobaLog.Core.Kql;

public static class KqlCompletionService
{
	public const int MaxItems = 50;

	// Allowlist mirrors KqlTransformer's operator switch — if it's not here, Apply throws
	// UnsupportedKqlException. Offering a forbidden operator in completions is a footgun: user
	// picks `join`, writes a query, gets a cryptic error instead of a "this operator isn't
	// supported" hint at pick time. Non-operator completions (columns, keywords, punctuation,
	// scalar functions) pass through unchanged.
	static readonly HashSet<string> SupportedQueryPrefixes = new(StringComparer.Ordinal)
	{
		"where",
		"take",
		"limit",   // Kusto alias for `take` → same TakeOperator node.
		"project",
		"extend",
		"count",
		"summarize",
		"sort",    // Kusto emits the operator name alone; `by` is a mandatory keyword that
		"order",   // follows, not part of the completion item display.
	};

	public static KqlCompletionsResponse Complete(string query, int position)
	{
		ArgumentNullException.ThrowIfNull(query);
		if (position < 0) position = 0;
		if (position > query.Length) position = query.Length;

		var svc = new KustoCodeService(query, KqlSchema.Globals);
		var info = svc.GetCompletionItems(position);

		var editStart = info.EditStart;
		var editLength = info.EditLength;
		var prefix = editLength > 0 && editStart >= 0 && editStart + editLength <= query.Length
			? query.Substring(editStart, editLength)
			: "";

		var filtered = info.Items
			.Where(i => string.IsNullOrEmpty(prefix)
				|| i.MatchText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.OrderBy(i => i.OrderText, StringComparer.Ordinal)
			.Select(ToCompletionItem)
			.Where(i => i is not null)
			.Select(i => i!)
			.Take(MaxItems)
			.ToList();

		return new KqlCompletionsResponse(editStart, editLength, filtered);
	}

	static KqlCompletionItem? ToCompletionItem(Kusto.Language.Editor.CompletionItem i)
	{
		// Drop query operators YobaLog's transformer doesn't handle. Columns, keywords, scalar
		// functions and punctuation kinds pass through without filtering.
		// Kusto tags start-of-pipeline operators as QueryPrefix (not QueryOperator — that's a
		// different beast). Drop prefixes outside the allowlist so users can't pick, e.g., `join`
		// and get a cryptic UnsupportedKqlException at Apply.
		if (i.Kind == CompletionKind.QueryPrefix && !SupportedQueryPrefixes.Contains(i.DisplayText))
			return null;

		// "Properties" alone isn't queryable — users need Properties.<key>. Replace the
		// insertion with "Properties." so the next completions round hits the property-key
		// discovery path on the server (admin.ts dispatches a keyup after inserting text that
		// ends in '.', so the handoff is seamless).
		if (i.DisplayText == "Properties")
			return new KqlCompletionItem("Column", "Properties", "Properties.", "");

		return new KqlCompletionItem(
			Kind: i.Kind.ToString(),
			DisplayText: i.DisplayText,
			BeforeText: i.BeforeText ?? "",
			AfterText: i.AfterText ?? "");
	}
}
