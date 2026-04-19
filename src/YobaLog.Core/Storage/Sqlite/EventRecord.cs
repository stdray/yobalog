using LinqToDB.Mapping;

namespace YobaLog.Core.Storage.Sqlite;

[Table("Events")]
sealed class EventRecord
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public long TimestampMs { get; set; }
	[Column, NotNull] public int Level { get; set; }
	[Column, NotNull] public string MessageTemplate { get; set; } = "";
	[Column, NotNull] public string Message { get; set; } = "";
	[Column, Nullable] public string? Exception { get; set; }
	[Column, Nullable] public string? TraceId { get; set; }
	[Column, Nullable] public string? SpanId { get; set; }
	[Column, Nullable] public int? EventId { get; set; }
	[Column, NotNull] public long TemplateHash { get; set; }
	[Column, NotNull] public string PropertiesJson { get; set; } = "{}";
}
