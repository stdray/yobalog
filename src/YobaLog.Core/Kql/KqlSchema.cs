using Kusto.Language;
using Kusto.Language.Symbols;

namespace YobaLog.Core.Kql;

public static class KqlSchema
{
	public const string DatabaseName = "yoba";

	public static readonly TableSymbol Events = new(
		KqlTransformer.EventsTable,
		new ColumnSymbol("Id", ScalarTypes.Long),
		new ColumnSymbol("Timestamp", ScalarTypes.DateTime),
		new ColumnSymbol("Level", ScalarTypes.Int),
		new ColumnSymbol("LevelName", ScalarTypes.String),
		new ColumnSymbol("TraceId", ScalarTypes.String),
		new ColumnSymbol("SpanId", ScalarTypes.String),
		new ColumnSymbol("Message", ScalarTypes.String),
		new ColumnSymbol("Exception", ScalarTypes.String),
		new ColumnSymbol("Properties", ScalarTypes.Dynamic));

	public static readonly GlobalState Globals = GlobalState.Default
		.WithDatabase(new DatabaseSymbol(DatabaseName, Events));
}
