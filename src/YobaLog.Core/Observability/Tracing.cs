using System.Diagnostics;

namespace YobaLog.Core.Observability;

// Four named ActivitySources per decision-log 2026-04-21 Phase G. Each module uses its
// own source, Web-side registration picks them all up via `t.AddSource("YobaLog.*")`.
//
// Rule: spans are emitted at batch boundaries only. Per-event instrumentation is banned
// (measured: 5-20% CPU at 100k events/sec — decision-log budget). The $system workspace
// is never instrumented — it's the destination of the exporter itself, so tracing its
// writes would recurse through the export loop.
public static class Tracing
{
	public const string IngestionSourceName = "YobaLog.Ingestion";
	public const string QuerySourceName = "YobaLog.Query";
	public const string RetentionSourceName = "YobaLog.Retention";
	public const string StorageSqliteSourceName = "YobaLog.Storage.Sqlite";

	public static readonly ActivitySource Ingestion = new(IngestionSourceName);
	public static readonly ActivitySource Query = new(QuerySourceName);
	public static readonly ActivitySource Retention = new(RetentionSourceName);
	public static readonly ActivitySource StorageSqlite = new(StorageSqliteSourceName);
}
