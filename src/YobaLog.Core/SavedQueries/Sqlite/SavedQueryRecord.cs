using LinqToDB.Mapping;

namespace YobaLog.Core.SavedQueries.Sqlite;

[Table("SavedQueries")]
sealed class SavedQueryRecord
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public string Name { get; set; } = "";
	[Column, NotNull] public string Kql { get; set; } = "";
	[Column, NotNull] public long CreatedAtMs { get; set; }
	[Column, NotNull] public long UpdatedAtMs { get; set; }
}
