using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;

namespace YobaLog.Tests.Auth;

public sealed class CompositeApiKeyStoreTests
{
	static ConfigApiKeyStore Config(params (string token, string workspace)[] keys) =>
		new(Options.Create(new ApiKeyOptions
		{
			Keys = [.. keys.Select(k => new ApiKeyConfig { Token = k.token, Workspace = k.workspace })],
		}));

	[Fact]
	public async Task Validate_FirstStoreMatch_ShortCircuits()
	{
		var first = Config(("root-token", "master"));
		var second = Config(("user-token", "ws1"));
		var composite = new CompositeApiKeyStore(first, second);

		var r = await composite.ValidateAsync("root-token", CancellationToken.None);
		r.IsValid.Should().BeTrue();
		r.Scope.Should().Be(WorkspaceId.Parse("master"));
	}

	[Fact]
	public async Task Validate_FallthroughToSecondStore()
	{
		var first = Config(("root-token", "master"));
		var second = Config(("user-token", "ws1"));
		var composite = new CompositeApiKeyStore(first, second);

		var r = await composite.ValidateAsync("user-token", CancellationToken.None);
		r.IsValid.Should().BeTrue();
		r.Scope.Should().Be(WorkspaceId.Parse("ws1"));
	}

	[Fact]
	public async Task Validate_UnknownToken_AllStoresFail()
	{
		var composite = new CompositeApiKeyStore(Config(("x", "a")), Config(("y", "b")));
		var r = await composite.ValidateAsync("z", CancellationToken.None);
		r.IsValid.Should().BeFalse();
	}

	[Fact]
	public async Task Validate_MissingToken_Invalid()
	{
		var composite = new CompositeApiKeyStore(Config(("x", "a")));
		(await composite.ValidateAsync(null, CancellationToken.None)).IsValid.Should().BeFalse();
		(await composite.ValidateAsync("", CancellationToken.None)).IsValid.Should().BeFalse();
	}

	[Fact]
	public void ConfiguredWorkspaces_IsUnionOfAllStores()
	{
		var composite = new CompositeApiKeyStore(
			Config(("a", "alpha"), ("b", "shared")),
			Config(("c", "beta"), ("d", "shared")));

		composite.ConfiguredWorkspaces.Should().BeEquivalentTo([
			WorkspaceId.Parse("alpha"),
			WorkspaceId.Parse("beta"),
			WorkspaceId.Parse("shared"),
		]);
	}
}
