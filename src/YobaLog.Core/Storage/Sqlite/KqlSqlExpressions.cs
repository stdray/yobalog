using LinqToDB;
using LinqToDB.SqlQuery;

namespace YobaLog.Core.Storage.Sqlite;

public static class KqlSqlExpressions
{
	[Sql.Expression(
		"{0} IN (SELECT rowid FROM EventsFts WHERE Message MATCH {2})",
		ServerSideOnly = false,
		IsPredicate = true,
		Precedence = Precedence.Comparison)]
	public static bool FtsHas(long id, string message, string term) =>
		HasWord(message, term);

	static bool HasWord(string? message, string term)
	{
		if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(term))
			return false;

		foreach (var token in Tokenize(message))
		{
			if (string.Equals(token, term, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	static IEnumerable<string> Tokenize(string s)
	{
		var start = -1;
		for (var i = 0; i <= s.Length; i++)
		{
			var isBoundary = i == s.Length || !char.IsLetterOrDigit(s[i]);
			if (isBoundary)
			{
				if (start >= 0 && i > start)
					yield return s[start..i];
				start = -1;
			}
			else if (start < 0)
			{
				start = i;
			}
		}
	}
}
