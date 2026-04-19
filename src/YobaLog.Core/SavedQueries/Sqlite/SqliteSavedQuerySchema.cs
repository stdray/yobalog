namespace YobaLog.Core.SavedQueries.Sqlite;

static class SqliteSavedQuerySchema
{
	public const string CreateTable = """
		CREATE TABLE IF NOT EXISTS SavedQueries (
			Id          INTEGER PRIMARY KEY AUTOINCREMENT,
			Name        TEXT NOT NULL,
			Kql         TEXT NOT NULL,
			CreatedAtMs INTEGER NOT NULL,
			UpdatedAtMs INTEGER NOT NULL
		);
		""";

	public const string CreateNameIndex =
		"CREATE UNIQUE INDEX IF NOT EXISTS ix_savedqueries_name ON SavedQueries(Name);";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateTable,
		CreateNameIndex,
	];
}
