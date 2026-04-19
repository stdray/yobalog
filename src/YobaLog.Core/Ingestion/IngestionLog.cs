using Microsoft.Extensions.Logging;

namespace YobaLog.Core.Ingestion;

static partial class IngestionLog
{
	[LoggerMessage(
		EventId = 1,
		Level = Microsoft.Extensions.Logging.LogLevel.Error,
		Message = "Failed to append batch of {Count} events to workspace {Workspace}")]
	public static partial void AppendBatchFailed(ILogger logger, Exception ex, int count, WorkspaceId workspace);

	[LoggerMessage(
		EventId = 2,
		Level = Microsoft.Extensions.Logging.LogLevel.Warning,
		Message = "Ingestion shutdown timed out; some batches may not have been flushed")]
	public static partial void ShutdownTimedOut(ILogger logger);
}
