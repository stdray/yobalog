using System.Collections.Immutable;
using System.Text.Json;
using YobaLog.Core.Storage;

namespace YobaLog.E2ETests.Infrastructure;

public static class SeedHelper
{
	static readonly ImmutableDictionary<string, JsonElement> EmptyProps =
		ImmutableDictionary<string, JsonElement>.Empty;

	public static LogEventCandidate Event(LogLevel level, string message, DateTimeOffset? at = null) =>
		new(
			Timestamp: at ?? DateTimeOffset.UtcNow,
			Level: level,
			MessageTemplate: message,
			Message: message,
			Exception: null,
			TraceId: null,
			SpanId: null,
			EventId: null,
			Properties: EmptyProps);

	public static async Task SeedAsync(this WebAppFixture fixture, string workspace, params LogEventCandidate[] events)
	{
		var ws = WorkspaceId.Parse(workspace);
		await fixture.LogStore.AppendBatchAsync(ws, events, CancellationToken.None);
	}
}
