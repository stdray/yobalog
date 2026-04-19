namespace YobaLog.Core.Sharing;

public sealed class ShareOptions
{
	public int MaxRows { get; set; } = 10_000;

	public int DefaultTtlHours { get; set; } = 24;
}
