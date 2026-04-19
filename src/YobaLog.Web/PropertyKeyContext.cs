using System.Text.RegularExpressions;

namespace YobaLog.Web;

static partial class PropertyKeyContext
{
	// Detects a cursor that's anchored to `Properties.<partial-key>` — the trailing `$`
	// keeps this sensitive to where the user is typing, not anywhere in the query.
	[GeneratedRegex(@"(?<!\w)Properties\.(?<prefix>[A-Za-z0-9_]*)$")]
	private static partial Regex Pattern();

	public static bool TryMatch(string query, int position, out int editStart, out string prefix)
	{
		editStart = 0;
		prefix = "";
		if (position < 0 || position > query.Length)
			return false;

		var head = query.AsSpan(0, position).ToString();
		var match = Pattern().Match(head);
		if (!match.Success)
			return false;

		prefix = match.Groups["prefix"].Value;
		editStart = position - prefix.Length;
		return true;
	}
}
