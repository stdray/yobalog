namespace YobaLog.Core.Auth.Sqlite;

// Lives in each workspace's `<ws>.meta.db` alongside saved queries, masking policies, share links.
// When the workspace is dropped, the file is deleted and these rows go with it.
static class SqliteApiKeySchema
{
    public const string CreateApiKeys = """
		CREATE TABLE IF NOT EXISTS ApiKeys (
			Id                TEXT PRIMARY KEY,
			TokenHash         TEXT NOT NULL UNIQUE,
			Prefix            TEXT NOT NULL,
			Title             TEXT,
			CreatedAtMs       INTEGER NOT NULL,
			IsWildcard        INTEGER NOT NULL DEFAULT 0,
			CanCreate         INTEGER NOT NULL DEFAULT 0,
			CreateWindowHours INTEGER NOT NULL DEFAULT 0
		);
		""";

    // Migration for existing databases that predate the wildcard columns.
    // Each entry: column name → ALTER TABLE SQL. The store checks pragma_table_info
    // before executing so the migration is idempotent without exception swallowing.
    public static readonly IReadOnlyList<(string Column, string Sql)> MigrateWildcardColumnMap =
    [
        ("IsWildcard",        "ALTER TABLE ApiKeys ADD COLUMN IsWildcard        INTEGER NOT NULL DEFAULT 0;"),
        ("CanCreate",         "ALTER TABLE ApiKeys ADD COLUMN CanCreate         INTEGER NOT NULL DEFAULT 0;"),
        ("CreateWindowHours", "ALTER TABLE ApiKeys ADD COLUMN CreateWindowHours INTEGER NOT NULL DEFAULT 0;"),
    ];

    public static readonly IReadOnlyList<string> AllStatements =
    [
        CreateApiKeys,
    ];
}
