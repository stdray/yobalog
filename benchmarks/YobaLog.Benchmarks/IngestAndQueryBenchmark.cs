using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Kusto.Language;
using Microsoft.Extensions.Logging.Abstractions;
using YobaLog.Core;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace YobaLog.Benchmarks;

[MemoryDiagnoser]
public class IngestAndQueryBenchmark
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("mixed");

	string _dir = "";
	SqliteLogStore _store = null!;
	LogEventCandidate[] _seed = [];
	LogEventCandidate[] _writeBatch = [];
	ChannelIngestionPipeline _pipeline = null!;
	KustoCode _queryByIndex = null!;

	const int SeedSize = 100_000;
	const int WriteBatchSize = 1_000;
	const int QueryRepeats = 20;

	[GlobalSetup]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "yobalog-mixed-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_dir);
		_store = new SqliteLogStore(MsOptions.Create(new SqliteLogStoreOptions { DataDirectory = _dir }));
		_store.CreateWorkspaceAsync(Ws, new WorkspaceSchema(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

		_seed = GenerateEvents(SeedSize, 0);
		_writeBatch = GenerateEvents(WriteBatchSize, SeedSize);
		_store.AppendBatchAsync(Ws, _seed, CancellationToken.None).AsTask().GetAwaiter().GetResult();

		_queryByIndex = KustoCode.Parse("LogEvents | where Level >= 4 | take 50");

		_pipeline = new ChannelIngestionPipeline(
			_store,
			MsOptions.Create(new IngestionOptions { ChannelCapacity = 20_000, MaxBatchSize = 1_000 }),
			NullLogger<ChannelIngestionPipeline>.Instance);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		try { _pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	static LogEventCandidate[] GenerateEvents(int count, int startId)
	{
		var arr = new LogEventCandidate[count];
		var baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (var i = 0; i < count; i++)
		{
			arr[i] = new LogEventCandidate(
				baseTs.AddSeconds(startId + i),
				(LogLevel)((startId + i) % 6),
				"evt",
				$"event {startId + i} happened",
				null, $"trace-{(startId + i) % 100}", null, null,
				ImmutableDictionary<string, JsonElement>.Empty);
		}
		return arr;
	}

	[Benchmark(Description = "Queries only (baseline latency, no concurrent writers)")]
	public async Task<long> QueriesOnly()
	{
		var total = 0L;
		for (var i = 0; i < QueryRepeats; i++)
		{
			await foreach (var _ in _store.QueryKqlAsync(Ws, _queryByIndex, CancellationToken.None))
				total++;
		}
		return total;
	}

	[Benchmark(Description = "Queries while 1k events ingest via pipeline in parallel")]
	public async Task<long> QueriesWhileIngesting()
	{
		var ingestTask = Task.Run(async () =>
		{
			await _pipeline.IngestAsync(Ws, _writeBatch, CancellationToken.None);
		});

		var queryTotal = 0L;
		var sw = Stopwatch.StartNew();
		for (var i = 0; i < QueryRepeats; i++)
		{
			await foreach (var _ in _store.QueryKqlAsync(Ws, _queryByIndex, CancellationToken.None))
				queryTotal++;
		}
		sw.Stop();

		await ingestTask;
		return queryTotal;
	}
}
