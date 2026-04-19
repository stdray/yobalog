namespace YobaLog.Core.Kql;

public sealed record KqlResult(
	IReadOnlyList<KqlColumn> Columns,
	IAsyncEnumerable<object?[]> Rows);
