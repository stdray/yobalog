using System.Diagnostics;

namespace YobaLog.Core.Observability;

// Four named ActivitySources per decision-log 2026-04-21 Phase G. Each module uses its
// own source, Web-side registration picks them all up via `t.AddSource("YobaLog.*")`.
//
// Rule: spans are emitted at batch boundaries only. Per-event instrumentation is banned
// (measured: 5-20% CPU at 100k events/sec — decision-log budget). The $system workspace
// is never instrumented — it's the destination of the exporter itself, so tracing its
// writes would recurse through the export loop.
//
// Storage layer: one `Storage` source for both logs and traces backends. Originally split
// into Storage.Sqlite + Storage.Traces, but the kind-of-storage axis is data, not identity
// — collapse into a single source and disambiguate via the `storage.kind` tag (logs|traces).
// Querying / dashboards filter `where Attributes.storage.kind == 'logs'` instead of
// `where Source.Name == 'YobaLog.Storage.Sqlite'`.
//
// Renamed from `Tracing` after Phase H added `YobaLog.Core.Tracing` namespace for the
// Span domain types — having a static class and a sibling namespace with the same
// short name breaks `using YobaLog.Core.Observability; Tracing.Ingestion.StartActivity(...)`
// resolution (C# picks the namespace first).
public static class ActivitySources
{
	public const string IngestionSourceName = "YobaLog.Ingestion";
	public const string QuerySourceName = "YobaLog.Query";
	public const string RetentionSourceName = "YobaLog.Retention";
	public const string StorageSourceName = "YobaLog.Storage";

	public static readonly ActivitySource Ingestion = new(IngestionSourceName);
	public static readonly ActivitySource Query = new(QuerySourceName);
	public static readonly ActivitySource Retention = new(RetentionSourceName);
	public static readonly ActivitySource Storage = new(StorageSourceName);
}
