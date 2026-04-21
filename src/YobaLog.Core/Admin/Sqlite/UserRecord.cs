using LinqToDB.Mapping;

namespace YobaLog.Core.Admin.Sqlite;

[Table("Users")]
sealed class UserRecord
{
	[Column, PrimaryKey] public string Username { get; set; } = "";
	[Column, NotNull] public string PasswordHash { get; set; } = "";
	[Column, NotNull] public long CreatedAtMs { get; set; }
}
