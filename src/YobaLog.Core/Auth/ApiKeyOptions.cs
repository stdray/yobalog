namespace YobaLog.Core.Auth;

public sealed record ApiKeyOptions
{
	public IReadOnlyList<ApiKeyConfig> Keys { get; init; } = [];
}

public sealed record ApiKeyConfig
{
	public string Token { get; init; } = "";
	public string Workspace { get; init; } = "";
	public string? Title { get; init; }
}
