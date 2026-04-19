using Kusto.Language;
using KustoLoco.Core;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

public sealed record TestEvent(
	long Id,
	DateTime Timestamp,
	string Level,
	string Message,
	string? TraceId = null,
	string? SpanId = null);

static class DualExecutor
{
	static readonly KqlTransformer Transformer = new();

	public static async Task AssertSameAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var refIds = await RunReferenceAsync(kql, dataset);
		var prodIds = RunProduction(kql, dataset);

		prodIds.Should().BeEquivalentTo(refIds,
			$"production and reference executors must agree on {kql}");
	}

	static async Task<IReadOnlyList<long>> RunReferenceAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var ctx = new KustoQueryContext();
		ctx.CopyDataIntoTable("LogEvents", dataset);

		var result = await ctx.RunQuery(kql);
		result.Error.Should().BeNullOrEmpty("reference executor error: " + result.Error);

		var idCol = Array.IndexOf(result.ColumnNames(), nameof(TestEvent.Id));
		return [.. result.EnumerateRows().Select(r => Convert.ToInt64(r[idCol]))];
	}

	static IReadOnlyList<long> RunProduction(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var records = dataset.Select(ToRecord).ToList();
		var code = KustoCode.Parse(kql);
		var result = Transformer.Apply(records.AsQueryable(), code).ToList();
		return [.. result.Select(r => r.Id)];
	}

	static EventRecord ToRecord(TestEvent t)
	{
		var level = LogLevelParser.Parse(t.Level)
			?? throw new ArgumentException($"unknown level '{t.Level}' in test data");
		return new EventRecord
		{
			Id = t.Id,
			TimestampMs = new DateTimeOffset(t.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
			Level = (int)level,
			Message = t.Message,
			MessageTemplate = t.Message,
			TraceId = t.TraceId,
			SpanId = t.SpanId,
			PropertiesJson = "{}",
		};
	}
}
