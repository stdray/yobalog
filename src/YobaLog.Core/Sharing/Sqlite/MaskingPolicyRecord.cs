using LinqToDB.Mapping;

namespace YobaLog.Core.Sharing.Sqlite;

[Table("FieldMaskingPolicy")]
sealed class MaskingPolicyRecord
{
	[Column, PrimaryKey] public string Path { get; set; } = "";
	[Column, NotNull] public int Mode { get; set; }
	[Column, NotNull] public long UpdatedAtMs { get; set; }
}
