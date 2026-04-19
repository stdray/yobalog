using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using YobaLog.Core;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace YobaLog.Benchmarks;

[MemoryDiagnoser]
public class IngestionPipelineBenchmark
{
	string _dir = "";
	SqliteLogStore _store = null!;
	LogEventCandidate[] _batch = [];
	WorkspaceId _ws;
	ChannelIngestionPipeline _pipeline = null!;

	[Params(1_000, 10_000, 100_000)]
	public int TotalEvents { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "yobalog-pipe-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_dir);
		_store = new SqliteLogStore(MsOptions.Create(new SqliteLogStoreOptions { DataDirectory = _dir }));

		_batch = new LogEventCandidate[TotalEvents];
		var baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (var i = 0; i < TotalEvents; i++)
		{
			_batch[i] = new LogEventCandidate(
				baseTs.AddSeconds(i),
				(LogLevel)(i % 6),
				"evt",
				$"event {i} happened",
				null, $"trace-{i % 100}", null, null,
				ImmutableDictionary<string, JsonElement>.Empty);
		}
	}

	[IterationSetup]
	public void IterSetup()
	{
		_ws = WorkspaceId.Parse("pipe-" + Guid.NewGuid().ToString("N")[..8]);
		_store.CreateWorkspaceAsync(_ws, new WorkspaceSchema(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
		_pipeline = new ChannelIngestionPipeline(
			_store,
			MsOptions.Create(new IngestionOptions { ChannelCapacity = 20_000, MaxBatchSize = 1_000 }),
			NullLogger<ChannelIngestionPipeline>.Instance);
	}

	[IterationCleanup]
	public void IterCleanup()
	{
		try { _pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
		try { _store.DropWorkspaceAsync(_ws, CancellationToken.None).AsTask().GetAwaiter().GetResult(); } catch { }
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	[Benchmark(Description = "Pipeline: IngestAsync + drain via StopAsync")]
	public async Task IngestPipeline()
	{
		await _pipeline.IngestAsync(_ws, _batch, CancellationToken.None);
		await _pipeline.StopAsync(CancellationToken.None);
	}

	[Benchmark(Description = "Direct ILogStore.AppendBatchAsync (reference)")]
	public async Task IngestDirect()
	{
		await _store.AppendBatchAsync(_ws, _batch, CancellationToken.None);
	}
}
