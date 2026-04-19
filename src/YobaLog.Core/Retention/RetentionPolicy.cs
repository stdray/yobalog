namespace YobaLog.Core.Retention;

public sealed record RetentionPolicy
{
	public string Workspace { get; init; } = "";
	public string SavedQuery { get; init; } = "";
	public int RetainDays { get; init; } = 7;
}
