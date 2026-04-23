using System.Collections.Immutable;
using System.Text.Json;
using Kusto.Language;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Kql;

namespace YobaLog.Tests.Ingestion;

public sealed class InMemoryTailBroadcasterTests
{
	static readonly WorkspaceId Ws = WorkspaceId.Parse("tail-ws");

	static LogEventCandidate Mk(LogLevel level, string msg = "m", string? trace = null) => new(
		DateTimeOffset.UtcNow,
		level,
		msg,
		msg,
		null,
		trace,
		null,
		null,
		ImmutableDictionary<string, JsonElement>.Empty);

	// Subscribe is `async IAsyncEnumerable` — the method body (including the
	// _subscribers.AddOrUpdate registration) only runs on the first MoveNextAsync,
	// up to the first real await (which is the channel reader). Priming the
	// enumerator ourselves is a deterministic barrier: by the time MoveNextAsync
	// returns (completed or pending), the subscriber is registered. Replaces a
	// racy Task.Delay that hoped Task.Run had reached Subscribe in time — on slow
	// CI that slept-too-short and Publish slipped past an empty subscriber list.
	static async Task<T> WithTimeoutAsync<T>(ValueTask<T> task, int seconds = 2) =>
		await task.AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));

	[Fact]
	public async Task Subscribe_ReceivesPublishedEvents_Unfiltered()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var allQuery = KustoCode.Parse("events");
		using var cts = new CancellationTokenSource();

		var enumerator = broadcaster.Subscribe(Ws, allQuery, cts.Token).GetAsyncEnumerator(cts.Token);
		var pending = enumerator.MoveNextAsync();
		broadcaster.Publish(Ws, [Mk(LogLevel.Information, "a"), Mk(LogLevel.Error, "b")]);

		(await WithTimeoutAsync(pending)).Should().BeTrue();
		enumerator.Current.Message.Should().Be("a");

		(await WithTimeoutAsync(enumerator.MoveNextAsync())).Should().BeTrue();
		enumerator.Current.Message.Should().Be("b");

		await cts.CancelAsync();
		await enumerator.DisposeAsync();
	}

	[Fact]
	public async Task Subscribe_FiltersByQuery()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var errorsOnly = KustoCode.Parse("events | where Level >= 4");
		using var cts = new CancellationTokenSource();

		var enumerator = broadcaster.Subscribe(Ws, errorsOnly, cts.Token).GetAsyncEnumerator(cts.Token);
		var pending = enumerator.MoveNextAsync();
		broadcaster.Publish(Ws,
			[
				Mk(LogLevel.Information, "skipped"),
				Mk(LogLevel.Warning, "also-skipped"),
				Mk(LogLevel.Error, "kept"),
			]);

		(await WithTimeoutAsync(pending)).Should().BeTrue();
		enumerator.Current.Message.Should().Be("kept");

		await cts.CancelAsync();
		await enumerator.DisposeAsync();
	}

	[Fact]
	public async Task MultipleSubscribers_EachReceiveEvents()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var allQuery = KustoCode.Parse("events");
		using var cts = new CancellationTokenSource();

		var enumA = broadcaster.Subscribe(Ws, allQuery, cts.Token).GetAsyncEnumerator(cts.Token);
		var enumB = broadcaster.Subscribe(Ws, allQuery, cts.Token).GetAsyncEnumerator(cts.Token);

		var pendA = enumA.MoveNextAsync();
		var pendB = enumB.MoveNextAsync();

		broadcaster.Publish(Ws, [Mk(LogLevel.Information, "shared")]);

		(await WithTimeoutAsync(pendA)).Should().BeTrue();
		(await WithTimeoutAsync(pendB)).Should().BeTrue();
		enumA.Current.Message.Should().Be("shared");
		enumB.Current.Message.Should().Be("shared");

		await cts.CancelAsync();
		await enumA.DisposeAsync();
		await enumB.DisposeAsync();
	}

	[Fact]
	public void Publish_NoSubscribers_NoOp()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var act = () => broadcaster.Publish(Ws, [Mk(LogLevel.Information, "ignored")]);
		act.Should().NotThrow();
	}

	[Fact]
	public void Subscribe_InvalidQuery_ThrowsAtSubscription()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var bad = KustoCode.Parse("events | summarize count()");

		var act = () =>
		{
			var enumerator = broadcaster.Subscribe(Ws, bad, CancellationToken.None).GetAsyncEnumerator();
			return enumerator.MoveNextAsync().AsTask();
		};
		act.Should().ThrowAsync<UnsupportedKqlException>();
	}
}
