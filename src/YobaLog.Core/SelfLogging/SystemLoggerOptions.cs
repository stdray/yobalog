using System.Collections.Immutable;
using System.Text.Json;

namespace YobaLog.Core.SelfLogging;

public sealed record SystemLoggerOptions
{
	public string CategoryPrefix { get; init; } = "YobaLog";
	public int QueueCapacity { get; init; } = 5_000;
	public int BatchSize { get; init; } = 200;
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
	public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

	// Stamped on every self-log event. Populated via PostConfigure in YobaLogApp
	// with App/Env/Ver/Sha/Host — matches yobaconf's logging-policy field taxonomy,
	// so $system events in yobalog UI filter the same way consumer-app events do.
	// `set` (not `init`) because IOptions Configure lambdas mutate post-binding.
	public ImmutableDictionary<string, JsonElement> StaticProperties { get; set; } =
		ImmutableDictionary<string, JsonElement>.Empty;
}
