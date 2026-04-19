namespace YobaLog.Core.Sharing.Sqlite;

static class SqliteShareLinkSchema
{
	public const string CreateTable = """
		CREATE TABLE IF NOT EXISTS ShareLinks (
			Id          TEXT PRIMARY KEY,
			Kql         TEXT NOT NULL,
			CreatedAtMs INTEGER NOT NULL,
			ExpiresAtMs INTEGER NOT NULL,
			Salt        BLOB NOT NULL,
			ColumnsJson TEXT NOT NULL,
			ModesJson   TEXT NOT NULL
		);
		""";

	public const string CreateExpiresIndex =
		"CREATE INDEX IF NOT EXISTS ix_sharelinks_expires ON ShareLinks(ExpiresAtMs);";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateTable,
		CreateExpiresIndex,
	];
}
