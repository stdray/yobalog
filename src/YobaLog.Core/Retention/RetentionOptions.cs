namespace YobaLog.Core.Retention;

public sealed record RetentionOptions
{
	public int RetentionDays { get; init; } = 7;
	public int SystemRetentionDays { get; init; } = 30;
	public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(1);
}
