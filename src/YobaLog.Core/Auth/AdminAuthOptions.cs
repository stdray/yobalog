namespace YobaLog.Core.Auth;

public sealed record AdminAuthOptions
{
	public string Username { get; init; } = "";

	/// <summary>Plaintext password fallback — use only for dev. Prefer PasswordHash for anything real.</summary>
	public string Password { get; init; } = "";

	/// <summary>`v1:{iter}:{base64 salt}:{base64 hash}` produced by AdminPasswordHasher.Hash. Wins over Password when set.</summary>
	public string PasswordHash { get; init; } = "";
}
