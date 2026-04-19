using System.Collections.Concurrent;
using System.Collections.Immutable;
using Kusto.Language;
using YobaLog.Core.Storage;

namespace YobaLog.Tests.Fakes;

public sealed class FakeLogStore : ILogStore
{
	readonly ConcurrentQueue<AppendCall> _appended = new();

	public ImmutableArray<AppendCall> Appended => [.. _appended];

	public Func<WorkspaceId, Task> AppendHook { get; set; } = _ => Task.CompletedTask;

	public async ValueTask AppendBatchAsync(
		WorkspaceId workspaceId,
		IReadOnlyList<LogEventCandidate> batch,
		CancellationToken ct)
	{
		await AppendHook(workspaceId).ConfigureAwait(false);
		_appended.Enqueue(new AppendCall(workspaceId, [.. batch]));
	}

	public IAsyncEnumerable<LogEvent> QueryAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct) =>
		throw new NotSupportedException();

	public IAsyncEnumerable<LogEvent> QueryKqlAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask<long> CountAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask<long> DeleteOlderThanAsync(WorkspaceId workspaceId, DateTimeOffset cutoff, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask DeclareIndexAsync(WorkspaceId workspaceId, string propertyPath, IndexKind kind, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, WorkspaceSchema schema, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask CompactAsync(WorkspaceId workspaceId, CancellationToken ct) =>
		throw new NotSupportedException();

	public ValueTask<WorkspaceStats> GetStatsAsync(WorkspaceId workspaceId, CancellationToken ct) =>
		throw new NotSupportedException();
}

public sealed record AppendCall(WorkspaceId Workspace, ImmutableArray<LogEventCandidate> Batch);
