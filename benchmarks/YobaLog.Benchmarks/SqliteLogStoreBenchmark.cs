using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Kusto.Language;
using MsOptions = Microsoft.Extensions.Options.Options;
using YobaLog.Core;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Benchmarks;

[MemoryDiagnoser]
public class SqliteLogStoreBenchmark
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("bench");

	string _dir = "";
	SqliteLogStore _store = null!;
	LogEventCandidate[] _batch = [];
	KustoCode _whereByIndex = null!;
	KustoCode _whereFts = null!;
	KustoCode _whereContains = null!;

	[Params(1_000, 100_000)]
	public int FixtureSize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "yobalog-bench-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_dir);
		_store = new SqliteLogStore(MsOptions.Create(new SqliteLogStoreOptions { DataDirectory = _dir }));
		_store.CreateWorkspaceAsync(Ws, new WorkspaceSchema(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

		_batch = new LogEventCandidate[FixtureSize];
		var baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (var i = 0; i < FixtureSize; i++)
		{
			_batch[i] = new LogEventCandidate(
				baseTs.AddSeconds(i),
				(LogLevel)(i % 6),
				"evt {{i}}",
				$"event {i} happened",
				null,
				$"trace-{i % 100}",
				null,
				null,
				ImmutableDictionary<string, JsonElement>.Empty);
		}

		_store.AppendBatchAsync(Ws, _batch, CancellationToken.None).AsTask().GetAwaiter().GetResult();

		_whereByIndex = KustoCode.Parse("LogEvents | where Level >= 4 | take 50");
		_whereFts = KustoCode.Parse("LogEvents | where Message has 'event' | take 50");
		_whereContains = KustoCode.Parse("LogEvents | where Message contains 'event' | take 50");
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(_dir, recursive: true); }
		catch { /* best effort */ }
	}

	[Benchmark]
	public async Task AppendBatchAsync()
	{
		// Creates a fresh workspace per batch to isolate write throughput.
		var ws = WorkspaceId.Parse("append-" + Guid.NewGuid().ToString("N")[..8]);
		await _store.CreateWorkspaceAsync(ws, new WorkspaceSchema(), CancellationToken.None);
		await _store.AppendBatchAsync(ws, _batch, CancellationToken.None);
		await _store.DropWorkspaceAsync(ws, CancellationToken.None);
	}

	[Benchmark]
	public async Task<int> QueryByIndex()
	{
		var n = 0;
		await foreach (var _ in _store.QueryKqlAsync(Ws, _whereByIndex, CancellationToken.None))
			n++;
		return n;
	}

	[Benchmark]
	public async Task<int> QueryFtsHas()
	{
		var n = 0;
		await foreach (var _ in _store.QueryKqlAsync(Ws, _whereFts, CancellationToken.None))
			n++;
		return n;
	}

	[Benchmark]
	public async Task<int> QueryContainsScan()
	{
		var n = 0;
		await foreach (var _ in _store.QueryKqlAsync(Ws, _whereContains, CancellationToken.None))
			n++;
		return n;
	}
}
