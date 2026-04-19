using BenchmarkDotNet.Attributes;
using Kusto.Language;
using YobaLog.Core;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Benchmarks;

[MemoryDiagnoser]
public class KqlTransformerBenchmark
{
	readonly KqlTransformer _transformer = new();

	EventRecord[] _rows = [];
	KustoCode _where = null!;
	KustoCode _whereTakeOrder = null!;
	KustoCode _project = null!;
	KustoCode _summarize = null!;
	KustoCode _count = null!;

	[Params(1_000, 100_000)]
	public int RowCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_rows = new EventRecord[RowCount];
		for (var i = 0; i < RowCount; i++)
		{
			_rows[i] = new EventRecord
			{
				Id = i,
				TimestampMs = i * 1000,
				Level = i % 6,
				MessageTemplate = "evt",
				Message = $"event {i}",
				TraceId = $"trace-{i % 100}",
			};
		}

		_where = KustoCode.Parse("LogEvents | where Level >= 4");
		_whereTakeOrder = KustoCode.Parse("LogEvents | where Level >= 3 | order by Id desc | take 50");
		_project = KustoCode.Parse("LogEvents | where Level >= 4 | project Id, Message");
		_summarize = KustoCode.Parse("LogEvents | summarize count() by Level");
		_count = KustoCode.Parse("LogEvents | where Level >= 3 | count");
	}

	[Benchmark]
	public int Where() => CountRows(_transformer.Apply(_rows.AsQueryable(), _where));

	[Benchmark]
	public int WhereTakeOrder() => CountRows(_transformer.Apply(_rows.AsQueryable(), _whereTakeOrder));

	[Benchmark]
	public async Task<int> Project() => await CountResultAsync(_transformer.Execute(_rows.AsQueryable(), _project));

	[Benchmark]
	public async Task<int> Summarize() => await CountResultAsync(_transformer.Execute(_rows.AsQueryable(), _summarize));

	[Benchmark]
	public async Task<int> Count() => await CountResultAsync(_transformer.Execute(_rows.AsQueryable(), _count));

	static int CountRows(IQueryable<EventRecord> query)
	{
		var n = 0;
		foreach (var _ in query)
			n++;
		return n;
	}

	static async Task<int> CountResultAsync(KqlResult result)
	{
		var n = 0;
		await foreach (var _ in result.Rows)
			n++;
		return n;
	}
}
