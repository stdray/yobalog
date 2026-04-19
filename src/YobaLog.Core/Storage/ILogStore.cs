using Kusto.Language;

namespace YobaLog.Core.Storage;

public interface ILogStore
{
	ValueTask AppendBatchAsync(WorkspaceId workspaceId, IReadOnlyList<LogEventCandidate> batch, CancellationToken ct);

	IAsyncEnumerable<LogEvent> QueryAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct);
	IAsyncEnumerable<LogEvent> QueryKqlAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct);
	ValueTask<long> CountAsync(WorkspaceId workspaceId, LogQuery query, CancellationToken ct);

	ValueTask<long> DeleteOlderThanAsync(WorkspaceId workspaceId, DateTimeOffset cutoff, CancellationToken ct);

	ValueTask DeclareIndexAsync(WorkspaceId workspaceId, string propertyPath, IndexKind kind, CancellationToken ct);

	ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, WorkspaceSchema schema, CancellationToken ct);
	ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
	ValueTask CompactAsync(WorkspaceId workspaceId, CancellationToken ct);
	ValueTask<WorkspaceStats> GetStatsAsync(WorkspaceId workspaceId, CancellationToken ct);
}
