using LinqToDB.Mapping;

namespace YobaLog.Core.Auth.Sqlite;

[Table("ApiKeys")]
sealed class ApiKeyRecord
{
    [Column, PrimaryKey] public string Id { get; set; } = "";
    [Column, NotNull] public string TokenHash { get; set; } = "";
    [Column, NotNull] public string Prefix { get; set; } = "";
    [Column] public string? Title { get; set; }
    [Column, NotNull] public long CreatedAtMs { get; set; }
    [Column, NotNull] public int IsWildcard { get; set; }
    [Column, NotNull] public int CanCreate { get; set; }
    [Column, NotNull] public int CreateWindowHours { get; set; }
}
