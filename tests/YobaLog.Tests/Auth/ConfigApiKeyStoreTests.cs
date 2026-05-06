using Microsoft.Extensions.Options;
using YobaLog.Core.Auth;

namespace YobaLog.Tests.Auth;

public sealed class ConfigApiKeyStoreTests
{
    static ConfigApiKeyStore Create(params ApiKeyConfig[] keys) =>
        new(Options.Create(new ApiKeyOptions { Keys = keys }));

    [Fact]
    public async Task Validate_ValidToken_ReturnsScope()
    {
        var store = Create(new ApiKeyConfig { Token = "abc", Workspace = "ws" });
        var r = await store.ValidateAsync("abc", CancellationToken.None);

        r.IsValid.Should().BeTrue();
        r.Scope.Should().Be(WorkspaceId.Parse("ws"));
    }

    [Fact]
    public async Task Validate_UnknownToken_Invalid()
    {
        var store = Create(new ApiKeyConfig { Token = "abc", Workspace = "ws" });
        var r = await store.ValidateAsync("xyz", CancellationToken.None);

        r.IsValid.Should().BeFalse();
        r.Scope.Should().BeNull();
    }

    [Fact]
    public async Task Validate_NullToken_Invalid()
    {
        var store = Create();
        var r = await store.ValidateAsync(null, CancellationToken.None);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_EmptyToken_Invalid()
    {
        var store = Create();
        var r = await store.ValidateAsync("", CancellationToken.None);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ConfiguredWorkspaces_DistinctFromKeys()
    {
        var store = Create(
            new ApiKeyConfig { Token = "k1", Workspace = "ws-a" },
            new ApiKeyConfig { Token = "k2", Workspace = "ws-a" },
            new ApiKeyConfig { Token = "k3", Workspace = "ws-b" });

        store.ConfiguredWorkspaces.Should().BeEquivalentTo(
            [WorkspaceId.Parse("ws-a"), WorkspaceId.Parse("ws-b")]);
    }

    [Fact]
    public void InvalidWorkspaceSlug_Skipped()
    {
        var store = Create(
            new ApiKeyConfig { Token = "k1", Workspace = "VALID-NOT" },
            new ApiKeyConfig { Token = "k2", Workspace = "ok" });

        store.ConfiguredWorkspaces.Should().BeEquivalentTo([WorkspaceId.Parse("ok")]);
    }

    [Fact]
    public void EmptyTokenOrWorkspace_Skipped()
    {
        var store = Create(
            new ApiKeyConfig { Token = "", Workspace = "x" },
            new ApiKeyConfig { Token = "k", Workspace = "" },
            new ApiKeyConfig { Token = "k", Workspace = "ok" });

        store.ConfiguredWorkspaces.Should().BeEquivalentTo([WorkspaceId.Parse("ok")]);
    }

    [Fact]
    public async Task WildcardWorkspace_Asterisk()
    {
        var store = Create(new ApiKeyConfig { Token = "wk", Workspace = "*", Title = "agent" });
        var r = await store.ValidateAsync("wk", CancellationToken.None);

        r.IsValid.Should().BeTrue();
        r.IsWildcard.Should().BeTrue();
        r.Scope.Should().BeNull();
        r.Title.Should().Be("agent");
    }

    [Fact]
    public async Task WildcardWithCreateWindow()
    {
        var store = Create(new ApiKeyConfig
        {
            Token = "wk",
            Workspace = "*",
            CanCreate = true,
            CreateWindowHours = 4,
            Title = "agent",
        });
        var r = await store.ValidateAsync("wk", CancellationToken.None);

        r.IsValid.Should().BeTrue();
        r.IsWildcard.Should().BeTrue();
        r.CanCreate.Should().BeTrue();
        r.Title.Should().Be("agent");
    }

    [Fact]
    public void WildcardKeys_NotInConfiguredWorkspaces()
    {
        var store = Create(
            new ApiKeyConfig { Token = "wk", Workspace = "*", Title = "agent" },
            new ApiKeyConfig { Token = "k1", Workspace = "ws-a" });

        store.ConfiguredWorkspaces.Should().BeEquivalentTo([WorkspaceId.Parse("ws-a")]);
    }
}
