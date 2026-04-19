using Kusto.Language;
using KustoLoco.Core;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

public sealed record TestEvent(
	long Id,
	DateTime Timestamp,
	int Level,
	string Message,
	string? TraceId = null,
	string? SpanId = null)
{
	public string LevelName => ((LogLevel)Level).ToString();

	public static TestEvent FromName(
		long id,
		DateTime ts,
		string levelName,
		string message,
		string? traceId = null,
		string? spanId = null) => new(
			id,
			ts,
			(int)(LogLevelParser.Parse(levelName) ?? throw new ArgumentException($"unknown level '{levelName}'")),
			message,
			traceId,
			spanId);
}

static class DualExecutor
{
	static readonly KqlTransformer Transformer = new();

	public static async Task AssertSameAsync(string kql, IReadOnlyList<TestEvent> dataset, bool ordered = false)
	{
		var refIds = await RunReferenceAsync(kql, dataset);
		var prodIds = RunProduction(kql, dataset);

		if (ordered)
		{
			prodIds.Should().ContainInOrder(refIds,
				$"production and reference must agree on order for {kql}");
			prodIds.Count.Should().Be(refIds.Count);
		}
		else
		{
			prodIds.Should().BeEquivalentTo(refIds,
				$"production and reference executors must agree on {kql}");
		}
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

	static EventRecord ToRecord(TestEvent t) => new()
	{
		Id = t.Id,
		TimestampMs = new DateTimeOffset(t.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = t.Level,
		Message = t.Message,
		MessageTemplate = t.Message,
		TraceId = t.TraceId,
		SpanId = t.SpanId,
		PropertiesJson = "{}",
	};
}
