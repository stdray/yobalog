using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Storage;
using YobaLog.Tests.Fakes;

namespace YobaLog.Tests;

public sealed class ChannelIngestionPipelineTests
{
	static readonly WorkspaceId Ws1 = WorkspaceId.Parse("ws-one");
	static readonly WorkspaceId Ws2 = WorkspaceId.Parse("ws-two");

	static LogEventCandidate Candidate(string msg = "m") => new(
		DateTimeOffset.UtcNow,
		LogLevel.Information,
		msg,
		msg,
		null,
		null,
		null,
		null,
		ImmutableDictionary<string, JsonElement>.Empty);

	static ChannelIngestionPipeline CreatePipeline(ILogStore store, int maxBatch = 1_000, int capacity = 10_000) =>
		new(
			store,
			Options.Create(new IngestionOptions { MaxBatchSize = maxBatch, ChannelCapacity = capacity }),
			NullLogger<ChannelIngestionPipeline>.Instance);

	[Fact]
	public async Task Ingest_SingleBatch_WrittenAfterStop()
	{
		var store = new FakeLogStore();
		await using var pipeline = CreatePipeline(store);

		await pipeline.IngestAsync(Ws1, [Candidate("a"), Candidate("b")], CancellationToken.None);
		await pipeline.StopAsync(CancellationToken.None);

		var allEvents = store.Appended.SelectMany(c => c.Batch).ToList();
		allEvents.Should().HaveCount(2);
		allEvents.Select(e => e.Message).Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public async Task Ingest_EmptyBatch_NoWrite()
	{
		var store = new FakeLogStore();
		await using var pipeline = CreatePipeline(store);

		await pipeline.IngestAsync(Ws1, [], CancellationToken.None);
		await pipeline.StopAsync(CancellationToken.None);

		store.Appended.Should().BeEmpty();
	}

	[Fact]
	public async Task Ingest_ManyEvents_AllWritten_NoLoss()
	{
		var store = new FakeLogStore();
		await using var pipeline = CreatePipeline(store, maxBatch: 50);

		const int total = 1_000;
		var candidates = Enumerable.Range(0, total).Select(i => Candidate($"m{i}")).ToList();
		await pipeline.IngestAsync(Ws1, candidates, CancellationToken.None);
		await pipeline.StopAsync(CancellationToken.None);

		var allEvents = store.Appended.SelectMany(c => c.Batch).ToList();
		allEvents.Should().HaveCount(total);
		allEvents.Select(e => e.Message).Should().BeEquivalentTo(candidates.Select(c => c.Message));
	}

	[Fact]
	public async Task Ingest_PreservesBatchSizeCap()
	{
		var store = new FakeLogStore();
		await using var pipeline = CreatePipeline(store, maxBatch: 10);

		var candidates = Enumerable.Range(0, 100).Select(i => Candidate($"m{i}")).ToList();
		await pipeline.IngestAsync(Ws1, candidates, CancellationToken.None);
		await pipeline.StopAsync(CancellationToken.None);

		store.Appended.Should().AllSatisfy(call => call.Batch.Length.Should().BeLessThanOrEqualTo(10));
		store.Appended.SelectMany(c => c.Batch).Should().HaveCount(100);
	}

	[Fact]
	public async Task Stop_Drains_RemainingEvents()
	{
		var store = new FakeLogStore();
		await using var pipeline = CreatePipeline(store, maxBatch: 5);

		var candidates = Enumerable.Range(0, 50).Select(i => Candidate($"m{i}")).ToList();
		await pipeline.IngestAsync(Ws1, candidates, CancellationToken.None);

		// Not polling; StopAsync must drain.
		await pipeline.StopAsync(CancellationToken.None);

		store.Appended.SelectMany(c => c.Batch).Should().HaveCount(50);
	}

	[Fact]
	public async Task Ingest_TwoWorkspaces_Isolation_SlowDoesNotBlockFast()
	{
		var slowHook = new TaskCompletionSource();
		var store = new FakeLogStore
		{
			AppendHook = ws => ws == Ws1 ? slowHook.Task : Task.CompletedTask,
		};
		await using var pipeline = CreatePipeline(store);

		await pipeline.IngestAsync(Ws1, [Candidate("slow-1"), Candidate("slow-2")], CancellationToken.None);
		await pipeline.IngestAsync(Ws2, [Candidate("fast-1"), Candidate("fast-2")], CancellationToken.None);

		// Ws2 should be processed even while Ws1 hangs.
		await WaitForAsync(
			() => store.Appended.Any(c => c.Workspace == Ws2),
			TimeSpan.FromSeconds(2));

		store.Appended.Should().Contain(c => c.Workspace == Ws2);
		store.Appended.Should().NotContain(c => c.Workspace == Ws1);

		// Release slow so shutdown can drain cleanly.
		slowHook.SetResult();
		await pipeline.StopAsync(CancellationToken.None);

		store.Appended.Should().Contain(c => c.Workspace == Ws1);
	}

	[Fact]
	public async Task Ingest_StoreThrows_ContinuesProcessing()
	{
		var callCount = 0;
		var store = new FakeLogStore
		{
			AppendHook = _ =>
			{
				if (Interlocked.Increment(ref callCount) == 1)
					throw new InvalidOperationException("boom");
				return Task.CompletedTask;
			},
		};
		await using var pipeline = CreatePipeline(store, maxBatch: 1);

		await pipeline.IngestAsync(
			Ws1,
			[Candidate("first"), Candidate("second"), Candidate("third")],
			CancellationToken.None);
		await pipeline.StopAsync(CancellationToken.None);

		// First batch threw and was dropped; second & third should still land.
		store.Appended.SelectMany(c => c.Batch).Select(e => e.Message).Should().Contain(["second", "third"]);
	}

	static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition())
				return;
			await Task.Delay(10).ConfigureAwait(false);
		}
		throw new TimeoutException("Condition not met within timeout");
	}
}
