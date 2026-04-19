namespace YobaLog.Core.Auth;

public sealed record AdminAuthOptions
{
	public string Username { get; init; } = "";
	public string Password { get; init; } = "";
}
