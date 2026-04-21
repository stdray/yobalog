using LinqToDB.Mapping;

namespace YobaLog.Core.Retention.Sqlite;

[Table("RetentionPolicies")]
sealed class RetentionPolicyRecord
{
	[Column, PrimaryKey(0)] public string Workspace { get; set; } = "";
	[Column, PrimaryKey(1)] public string SavedQuery { get; set; } = "";
	[Column, NotNull] public int RetainDays { get; set; }
}
