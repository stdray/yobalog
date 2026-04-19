using System.Text.Json;
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

	// Maps to SQLite json_extract(json, path). Used by KQL `Properties.<key>` access.
	// Reference executor (dual-executor tests, live-tail broadcaster predicates) runs the C# body.
	[Sql.Expression("json_extract({0}, {1})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? JsonExtract(string? json, string path) =>
		InMemoryJsonExtract(json, path);

	static string? InMemoryJsonExtract(string? json, string? path)
	{
		if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path) || !path.StartsWith("$.", StringComparison.Ordinal))
			return null;
		var key = path[2..];
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var prop))
				return null;
			return prop.ValueKind switch
			{
				JsonValueKind.String => prop.GetString(),
				JsonValueKind.Null or JsonValueKind.Undefined => null,
				_ => prop.GetRawText(),
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

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
