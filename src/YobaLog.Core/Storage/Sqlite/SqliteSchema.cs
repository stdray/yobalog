namespace YobaLog.Core.Storage.Sqlite;

static class SqliteSchema
{
	public const string CreateEventsTable = """
		CREATE TABLE IF NOT EXISTS Events (
			Id              INTEGER PRIMARY KEY AUTOINCREMENT,
			TimestampMs     INTEGER NOT NULL,
			Level           INTEGER NOT NULL,
			MessageTemplate TEXT    NOT NULL,
			Message         TEXT    NOT NULL,
			Exception       TEXT,
			TraceId         TEXT,
			SpanId          TEXT,
			EventId         INTEGER,
			TemplateHash    INTEGER NOT NULL,
			PropertiesJson  TEXT    NOT NULL DEFAULT '{}'
		);
		""";

	public const string CreateTimestampIndex =
		"CREATE INDEX IF NOT EXISTS ix_events_ts_id ON Events(TimestampMs DESC, Id DESC);";

	public const string CreateLevelIndex =
		"CREATE INDEX IF NOT EXISTS ix_events_level ON Events(Level);";

	public const string CreateTraceIdIndex =
		"CREATE INDEX IF NOT EXISTS ix_events_trace ON Events(TraceId) WHERE TraceId IS NOT NULL;";

	public const string CreateSpanIdIndex =
		"CREATE INDEX IF NOT EXISTS ix_events_span ON Events(SpanId) WHERE SpanId IS NOT NULL;";

	public const string CreateTemplateHashIndex =
		"CREATE INDEX IF NOT EXISTS ix_events_template ON Events(TemplateHash);";

	public const string CreateFtsTable = """
		CREATE VIRTUAL TABLE IF NOT EXISTS EventsFts USING fts5(
			Message,
			content='Events',
			content_rowid='Id'
		);
		""";

	public const string CreateFtsInsertTrigger = """
		CREATE TRIGGER IF NOT EXISTS Events_ai AFTER INSERT ON Events BEGIN
			INSERT INTO EventsFts(rowid, Message) VALUES (new.Id, new.Message);
		END;
		""";

	public const string CreateFtsDeleteTrigger = """
		CREATE TRIGGER IF NOT EXISTS Events_ad AFTER DELETE ON Events BEGIN
			INSERT INTO EventsFts(EventsFts, rowid, Message) VALUES('delete', old.Id, old.Message);
		END;
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateEventsTable,
		CreateTimestampIndex,
		CreateLevelIndex,
		CreateTraceIdIndex,
		CreateSpanIdIndex,
		CreateTemplateHashIndex,
		CreateFtsTable,
		CreateFtsInsertTrigger,
		CreateFtsDeleteTrigger,
	];
}
