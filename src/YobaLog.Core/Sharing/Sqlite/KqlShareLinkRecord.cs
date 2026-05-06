using LinqToDB.Mapping;

namespace YobaLog.Core.Sharing.Sqlite;

[Table("KqlShareLinks")]
sealed class KqlShareLinkRecord
{
    [Column, PrimaryKey] public string Id { get; set; } = "";
    [Column, NotNull] public string Workspace { get; set; } = "";
    [Column, NotNull] public string Kql { get; set; } = "";
    [Column, NotNull] public long CreatedAtMs { get; set; }
    [Column, NotNull] public long ExpiresAtMs { get; set; }
}
