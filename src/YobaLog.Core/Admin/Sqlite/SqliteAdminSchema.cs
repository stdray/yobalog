namespace YobaLog.Core.Admin.Sqlite;

// Admin tables live in $system.meta.db — a single DB owns the global catalog (workspaces,
// api-keys, users, retention policies). Keeps per-workspace .meta.db files narrow.
static class SqliteAdminSchema
{
	public const string CreateWorkspaces = """
		CREATE TABLE IF NOT EXISTS Workspaces (
			Id          TEXT PRIMARY KEY,
			CreatedAtMs INTEGER NOT NULL
		);
		""";

	public const string CreateUsers = """
		CREATE TABLE IF NOT EXISTS Users (
			Username     TEXT PRIMARY KEY,
			PasswordHash TEXT NOT NULL,
			CreatedAtMs  INTEGER NOT NULL
		);
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateWorkspaces,
		CreateUsers,
	];
}
