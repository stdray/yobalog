namespace YobaLog.Core.Retention;

public sealed record RetentionOptions
{
	public int DefaultRetainDays { get; init; } = 7;
	public int SystemRetainDays { get; init; } = 30;
	public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(1);
	public IReadOnlyList<RetentionPolicy> Policies { get; init; } = [];
}
