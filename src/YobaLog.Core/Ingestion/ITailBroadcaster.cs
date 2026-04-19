using Kusto.Language;

namespace YobaLog.Core.Ingestion;

public interface ITailBroadcaster
{
	/// <summary>
	/// Subscribe to live events in a workspace, filtered by a KQL query.
	/// Pass <c>KustoCode.Parse("LogEvents")</c> for an unfiltered stream.
	/// Shape-changing operators (project/summarize/count/extend) are rejected —
	/// they make no sense on a streaming event feed.
	/// </summary>
	IAsyncEnumerable<LogEventCandidate> Subscribe(WorkspaceId workspaceId, KustoCode query, CancellationToken ct);

	void Publish(WorkspaceId workspaceId, IReadOnlyList<LogEventCandidate> batch);
}
