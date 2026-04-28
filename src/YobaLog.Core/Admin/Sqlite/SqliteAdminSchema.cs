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

    public const string CreateRetentionPolicies = """
		CREATE TABLE IF NOT EXISTS RetentionPolicies (
			Workspace   TEXT NOT NULL,
			SavedQuery  TEXT NOT NULL,
			RetainDays  INTEGER NOT NULL,
			PRIMARY KEY (Workspace, SavedQuery)
		);
		""";

    // Personal access tokens for `/v1/admin/*`. Lives in $system.meta.db next to Users so the
    // cascade-on-user-delete handler can run both deletes in a single transaction.
    public const string CreateAdminTokens = """
		CREATE TABLE IF NOT EXISTS AdminTokens (
			Id            INTEGER PRIMARY KEY AUTOINCREMENT,
			Username      TEXT NOT NULL,
			TokenHash     TEXT NOT NULL UNIQUE,
			TokenPrefix   TEXT NOT NULL,
			Description   TEXT NOT NULL,
			UpdatedAtMs   INTEGER NOT NULL,
			IsDeleted     INTEGER NOT NULL DEFAULT 0
		);
		""";

    public const string CreateAdminTokensUsernameIndex =
        "CREATE INDEX IF NOT EXISTS IX_AdminTokens_Username ON AdminTokens(Username);";

    public static readonly IReadOnlyList<string> AllStatements =
    [
        CreateWorkspaces,
        CreateUsers,
        CreateRetentionPolicies,
        CreateAdminTokens,
        CreateAdminTokensUsernameIndex,
    ];
}
