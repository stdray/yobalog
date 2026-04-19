namespace YobaLog.Core.Ingestion;

public interface IIngestionPipeline
{
	ValueTask IngestAsync(WorkspaceId workspaceId, IReadOnlyList<LogEventCandidate> batch, CancellationToken ct);
}
