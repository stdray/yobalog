namespace YobaLog.Core.Sharing.Sqlite;

static class SqliteMaskingPolicySchema
{
	public const string CreateTable = """
		CREATE TABLE IF NOT EXISTS FieldMaskingPolicy (
			Path        TEXT PRIMARY KEY,
			Mode        INTEGER NOT NULL,
			UpdatedAtMs INTEGER NOT NULL
		);
		""";

	// Early builds stored property paths with a "Properties." prefix; the namespace is now flat
	// (see TsvExporter). Strip the prefix, preferring bare row if both already exist (INSERT OR IGNORE).
	public const string MigrateFlatPaths = """
		INSERT OR IGNORE INTO FieldMaskingPolicy (Path, Mode, UpdatedAtMs)
			SELECT substr(Path, 12), Mode, UpdatedAtMs FROM FieldMaskingPolicy WHERE Path LIKE 'Properties.%';
		DELETE FROM FieldMaskingPolicy WHERE Path LIKE 'Properties.%';
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateTable,
		MigrateFlatPaths,
	];
}
