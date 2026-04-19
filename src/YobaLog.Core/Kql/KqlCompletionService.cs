using Kusto.Language.Editor;

namespace YobaLog.Core.Kql;

#pragma warning disable CA1822 // Instance-scoped for future options/DI (see KqlTransformer rationale).

public sealed class KqlCompletionService
{
	public const int MaxItems = 50;

	public KqlCompletionsResponse Complete(string query, int position)
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
			.Take(MaxItems)
			.Select(i => new KqlCompletionItem(
				Kind: i.Kind.ToString(),
				DisplayText: i.DisplayText,
				BeforeText: i.BeforeText ?? "",
				AfterText: i.AfterText ?? ""))
			.ToList();

		return new KqlCompletionsResponse(editStart, editLength, filtered);
	}
}
