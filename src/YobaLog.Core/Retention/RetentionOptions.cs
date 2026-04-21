namespace YobaLog.Core.Retention;

public sealed record RetentionOptions
{
	public int DefaultRetainDays { get; init; } = 7;
	public int SystemRetainDays { get; init; } = 30;
	// Span retention defaults mirror log retention unless overridden. Decision-log 2026-04-21
	// calls out the typical operator ask: "logs 30 days, traces 7" — set at deployment time
	// via Retention:DefaultSpansRetainDays in appsettings.
	public int DefaultSpansRetainDays { get; init; } = 7;
	public int SystemSpansRetainDays { get; init; } = 30;
	public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(1);
	public IReadOnlyList<RetentionPolicy> Policies { get; init; } = [];
}
