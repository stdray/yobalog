using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Auth.Sqlite;
using YobaLog.Tests.Fakes;

namespace YobaLog.Tests.Auth;

public sealed class SqliteApiKeyStoreTests : IAsyncLifetime
{
    readonly string _tempDir;
    readonly ServiceProvider _services;
    readonly SqliteApiKeyStore _store;
    static readonly WorkspaceId WsA = WorkspaceId.Parse("keys-a");
    static readonly WorkspaceId WsB = WorkspaceId.Parse("keys-b");

    public SqliteApiKeyStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yobalog-apikeys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _services = TestServices.BuildSqliteStores(_tempDir);
        _store = _services.GetRequiredService<SqliteApiKeyStore>();
    }

    public async Task InitializeAsync()
    {
        await _store.InitializeWorkspaceAsync(WsA, CancellationToken.None);
        await _store.InitializeWorkspaceAsync(WsB, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Create_ReturnsPlaintext_ValidateWorks()
    {
        var created = await _store.CreateAsync(WsA, "prod-api", CancellationToken.None);

        created.Plaintext.Should().HaveLength(22); // ShortGuid — base64url of 16 random bytes
        created.Info.Prefix.Should().HaveLength(6);
        created.Info.Prefix.Should().Be(created.Plaintext[..6]);
        created.Info.Workspace.Should().Be(WsA);
        created.Info.Title.Should().Be("prod-api");

        var r = await _store.ValidateAsync(created.Plaintext, CancellationToken.None);
        r.IsValid.Should().BeTrue();
        r.Scope.Should().Be(WsA);
    }

    [Fact]
    public async Task Validate_UnknownToken_Invalid()
    {
        await _store.CreateAsync(WsA, null, CancellationToken.None);
        var r = await _store.ValidateAsync("definitely-not-a-real-token", CancellationToken.None);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_MissingToken_Invalid()
    {
        var r = await _store.ValidateAsync(null, CancellationToken.None);
        r.IsValid.Should().BeFalse();

        r = await _store.ValidateAsync("", CancellationToken.None);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task List_ReturnsOnlyThisWorkspace()
    {
        await _store.CreateAsync(WsA, "alpha", CancellationToken.None);
        await _store.CreateAsync(WsA, "beta", CancellationToken.None);
        await _store.CreateAsync(WsB, "gamma", CancellationToken.None);

        var a = await _store.ListAsync(WsA, CancellationToken.None);
        var b = await _store.ListAsync(WsB, CancellationToken.None);

        a.Should().HaveCount(2);
        a.Select(k => k.Title).Should().BeEquivalentTo("alpha", "beta");
        b.Should().HaveCount(1);
        b[0].Title.Should().Be("gamma");
    }

    [Fact]
    public async Task Delete_EvictsFromCacheAndStorage()
    {
        var created = await _store.CreateAsync(WsA, null, CancellationToken.None);

        var deleted = await _store.DeleteAsync(WsA, created.Info.Id, CancellationToken.None);
        deleted.Should().BeTrue();

        (await _store.ValidateAsync(created.Plaintext, CancellationToken.None)).IsValid.Should().BeFalse();
        (await _store.ListAsync(WsA, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_MissingId_ReturnsFalse()
    {
        var deleted = await _store.DeleteAsync(WsA, "no-such-id", CancellationToken.None);
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ConfiguredWorkspaces_ReflectsPresence()
    {
        _store.ConfiguredWorkspaces.Should().BeEmpty();

        var a1 = await _store.CreateAsync(WsA, null, CancellationToken.None);
        _store.ConfiguredWorkspaces.Should().BeEquivalentTo([WsA]);

        await _store.CreateAsync(WsB, null, CancellationToken.None);
        _store.ConfiguredWorkspaces.Should().BeEquivalentTo([WsA, WsB]);

        await _store.DeleteAsync(WsA, a1.Info.Id, CancellationToken.None);
        _store.ConfiguredWorkspaces.Should().BeEquivalentTo([WsB]);
    }

    [Fact]
    public async Task DropWorkspace_EvictsAllKeysForWorkspace()
    {
        var a1 = await _store.CreateAsync(WsA, null, CancellationToken.None);
        var a2 = await _store.CreateAsync(WsA, null, CancellationToken.None);
        var b1 = await _store.CreateAsync(WsB, null, CancellationToken.None);

        await _store.DropWorkspaceAsync(WsA, CancellationToken.None);

        (await _store.ValidateAsync(a1.Plaintext, CancellationToken.None)).IsValid.Should().BeFalse();
        (await _store.ValidateAsync(a2.Plaintext, CancellationToken.None)).IsValid.Should().BeFalse();
        (await _store.ValidateAsync(b1.Plaintext, CancellationToken.None)).IsValid.Should().BeTrue();

        _store.ConfiguredWorkspaces.Should().BeEquivalentTo([WsB]);
        File.Exists(Path.Combine(_tempDir, $"{WsA.Value}.meta.db")).Should().BeFalse();
    }

    [Fact]
    public async Task CacheRebuildsFromDisk_AfterReinitialize()
    {
        var created = await _store.CreateAsync(WsA, "persisted", CancellationToken.None);

        // Simulate process restart — new store instance over the same data directory.
        await using var reopenedServices = TestServices.BuildSqliteStores(_tempDir);
        var reopened = reopenedServices.GetRequiredService<SqliteApiKeyStore>();
        await reopened.InitializeWorkspaceAsync(WsA, CancellationToken.None);

        var r = await reopened.ValidateAsync(created.Plaintext, CancellationToken.None);
        r.IsValid.Should().BeTrue();
        r.Scope.Should().Be(WsA);
    }
}
