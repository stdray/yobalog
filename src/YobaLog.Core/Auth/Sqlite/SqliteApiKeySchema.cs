namespace YobaLog.Core.Auth.Sqlite;

// Lives in each workspace's `<ws>.meta.db` alongside saved queries, masking policies, share links.
// When the workspace is dropped, the file is deleted and these rows go with it.
static class SqliteApiKeySchema
{
	public const string CreateApiKeys = """
		CREATE TABLE IF NOT EXISTS ApiKeys (
			Id          TEXT PRIMARY KEY,
			TokenHash   TEXT NOT NULL UNIQUE,
			Prefix      TEXT NOT NULL,
			Title       TEXT,
			CreatedAtMs INTEGER NOT NULL
		);
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateApiKeys,
	];
}
