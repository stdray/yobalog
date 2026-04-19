namespace YobaLog.Core.SelfLogging;

public sealed record SystemLoggerOptions
{
	public string CategoryPrefix { get; init; } = "YobaLog";
	public int QueueCapacity { get; init; } = 5_000;
	public int BatchSize { get; init; } = 200;
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
	public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
}
