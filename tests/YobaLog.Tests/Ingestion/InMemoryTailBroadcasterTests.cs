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

	[Fact]
	public async Task Subscribe_ReceivesPublishedEvents_Unfiltered()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var allQuery = KustoCode.Parse("LogEvents");
		using var cts = new CancellationTokenSource();

		var received = new List<LogEventCandidate>();
		var task = Task.Run(async () =>
		{
			await foreach (var e in broadcaster.Subscribe(Ws, allQuery, cts.Token))
			{
				received.Add(e);
				if (received.Count >= 2) break;
			}
		});

		await Task.Delay(50);
		broadcaster.Publish(Ws, [Mk(LogLevel.Information, "a"), Mk(LogLevel.Error, "b")]);

		await task.WaitAsync(TimeSpan.FromSeconds(2));
		received.Should().HaveCount(2);
		received.Select(e => e.Message).Should().ContainInOrder("a", "b");
	}

	[Fact]
	public async Task Subscribe_FiltersByQuery()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var errorsOnly = KustoCode.Parse("LogEvents | where Level >= 4");
		using var cts = new CancellationTokenSource();

		var received = new List<LogEventCandidate>();
		var task = Task.Run(async () =>
		{
			await foreach (var e in broadcaster.Subscribe(Ws, errorsOnly, cts.Token))
			{
				received.Add(e);
				if (received.Count >= 1) break;
			}
		});

		await Task.Delay(50);
		broadcaster.Publish(Ws,
			[
				Mk(LogLevel.Information, "skipped"),
				Mk(LogLevel.Warning, "also-skipped"),
				Mk(LogLevel.Error, "kept"),
			]);

		await task.WaitAsync(TimeSpan.FromSeconds(2));
		received.Single().Message.Should().Be("kept");
	}

	[Fact]
	public async Task MultipleSubscribers_EachReceiveEvents()
	{
		var broadcaster = new InMemoryTailBroadcaster();
		var allQuery = KustoCode.Parse("LogEvents");
		using var cts = new CancellationTokenSource();

		var subA = new List<LogEventCandidate>();
		var subB = new List<LogEventCandidate>();

		var taskA = Task.Run(async () =>
		{
			await foreach (var e in broadcaster.Subscribe(Ws, allQuery, cts.Token))
			{
				subA.Add(e);
				if (subA.Count >= 1) break;
			}
		});
		var taskB = Task.Run(async () =>
		{
			await foreach (var e in broadcaster.Subscribe(Ws, allQuery, cts.Token))
			{
				subB.Add(e);
				if (subB.Count >= 1) break;
			}
		});

		await Task.Delay(100);
		broadcaster.Publish(Ws, [Mk(LogLevel.Information, "shared")]);

		await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(2));
		subA.Single().Message.Should().Be("shared");
		subB.Single().Message.Should().Be("shared");
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
		var bad = KustoCode.Parse("LogEvents | summarize count()");

		var act = () =>
		{
			var enumerator = broadcaster.Subscribe(Ws, bad, CancellationToken.None).GetAsyncEnumerator();
			return enumerator.MoveNextAsync().AsTask();
		};
		act.Should().ThrowAsync<UnsupportedKqlException>();
	}
}
