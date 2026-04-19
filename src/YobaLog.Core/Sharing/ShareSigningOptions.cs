namespace YobaLog.Core.Sharing;

public sealed class ShareSigningOptions
{
	/// <summary>Base64-encoded HMAC-SHA256 signing key. At least 32 bytes.</summary>
	public string Key { get; set; } = "";

	public int MaxRows { get; set; } = 10_000;

	public int DefaultTtlHours { get; set; } = 24;
}
