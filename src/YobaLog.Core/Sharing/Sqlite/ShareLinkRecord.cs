using LinqToDB.Mapping;

namespace YobaLog.Core.Sharing.Sqlite;

[Table("ShareLinks")]
sealed class ShareLinkRecord
{
	[Column, PrimaryKey] public string Id { get; set; } = "";
	[Column, NotNull] public string Kql { get; set; } = "";
	[Column, NotNull] public long CreatedAtMs { get; set; }
	[Column, NotNull] public long ExpiresAtMs { get; set; }
	[Column, NotNull] public byte[] Salt { get; set; } = [];
	[Column, NotNull] public string ColumnsJson { get; set; } = "[]";
	[Column, NotNull] public string ModesJson { get; set; } = "{}";
}
