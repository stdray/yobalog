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

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateTable,
	];
}
