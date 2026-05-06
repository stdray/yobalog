using LinqToDB.Mapping;

namespace YobaLog.Core.Admin.Sqlite;

[Table("Workspaces")]
sealed class WorkspaceRecord
{
    [Column, PrimaryKey] public string Id { get; set; } = "";
    [Column, NotNull] public long CreatedAtMs { get; set; }
    [Column, NotNull] public string Description { get; set; } = "";
    [Column, NotNull] public string Agent { get; set; } = "";
    [Column, NotNull] public string GroupName { get; set; } = "";
}
