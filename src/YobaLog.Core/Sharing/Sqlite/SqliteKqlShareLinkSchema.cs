namespace YobaLog.Core.Sharing.Sqlite;

static class SqliteKqlShareLinkSchema
{
    public const string CreateTable = """
		CREATE TABLE IF NOT EXISTS KqlShareLinks (
			Id          TEXT PRIMARY KEY,
			Workspace   TEXT NOT NULL,
			Kql         TEXT NOT NULL,
			CreatedAtMs INTEGER NOT NULL,
			ExpiresAtMs INTEGER NOT NULL
		);
		""";

    public const string CreateExpiresIndex =
        "CREATE INDEX IF NOT EXISTS ix_kqlsharelinks_expires ON KqlShareLinks(ExpiresAtMs);";

    public static readonly IReadOnlyList<string> AllStatements =
    [
        CreateTable,
        CreateExpiresIndex,
    ];
}
