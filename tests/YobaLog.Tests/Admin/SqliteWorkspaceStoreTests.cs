using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Admin;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Auth.Sqlite;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Sharing;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing.Sqlite;
using YobaLog.Tests.Fakes;

namespace YobaLog.Tests.Admin;

public sealed class SqliteWorkspaceStoreTests : IAsyncLifetime
{
    readonly string _tempDir;
    readonly ServiceProvider _services;
    readonly SqliteApiKeyStore _apiKeys;
    readonly SqliteSavedQueryStore _savedQueries;
    readonly SqliteFieldMaskingPolicyStore _masking;
    readonly SqliteShareLinkStore _shareLinks;
    readonly SqliteWorkspaceStore _store;
    static readonly WorkspaceId Ws = WorkspaceId.Parse("admin-create");

    public SqliteWorkspaceStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yobalog-ws-admin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _services = TestServices.BuildSqliteStores(_tempDir);
        _apiKeys = _services.GetRequiredService<SqliteApiKeyStore>();
        _savedQueries = _services.GetRequiredService<SqliteSavedQueryStore>();
        _masking = _services.GetRequiredService<SqliteFieldMaskingPolicyStore>();
        _shareLinks = _services.GetRequiredService<SqliteShareLinkStore>();
        _store = _services.GetRequiredService<SqliteWorkspaceStore>();
    }

    public async Task InitializeAsync() => await _store.InitializeAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateAsync_Initializes_AllMetaTables()
    {
        await _store.CreateAsync(Ws, ct: CancellationToken.None);

        // Each meta store's list/get must not throw "no such table" — this is the bug that bit
        // admin-created workspaces in prod (GET /ws/{newly-created} → SqliteException on
        // SavedQueries because only the api-keys meta was initialized).
        var savedQueries = await _savedQueries.ListAsync(Ws, CancellationToken.None);
        savedQueries.Should().BeEmpty();

        var policy = await _masking.GetAsync(Ws, CancellationToken.None);
        policy.Modes.Should().BeEmpty();

        var shareLink = await _shareLinks.GetAsync(Ws, "nonexistent", CancellationToken.None);
        shareLink.Should().BeNull();

        var apiKeys = await _apiKeys.ListAsync(Ws, CancellationToken.None);
        apiKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_Drops_AllMetaFiles()
    {
        await _store.CreateAsync(Ws, ct: CancellationToken.None);

        var deleted = await _store.DeleteAsync(Ws, CancellationToken.None);
        deleted.Should().BeTrue();

        // Both logs.db and meta.db should be gone (api-keys + saved queries + masking + share
        // links all live in meta.db → deleting the file removes all four).
        File.Exists(Path.Combine(_tempDir, $"{Ws.Value}.logs.db")).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, $"{Ws.Value}.meta.db")).Should().BeFalse();
    }

    [Fact]
    public async Task GetOrCreateAsync_Creates_WhenMissing()
    {
        var info = await _store.GetOrCreateAsync(Ws,
            "test description", "test-agent", "test-group", CancellationToken.None);

        info.Id.Should().Be(Ws);
        info.Description.Should().Be("test description");
        info.Agent.Should().Be("test-agent");
        info.GroupName.Should().Be("test-group");
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsExisting_WhenPresent()
    {
        var created = await _store.GetOrCreateAsync(Ws,
            "first desc", "agent-1", "group-1", CancellationToken.None);

        var existing = await _store.GetOrCreateAsync(Ws,
            "ignored desc", "ignored-agent", "ignored-group", CancellationToken.None);

        existing.Id.Should().Be(Ws);
        existing.Description.Should().Be("first desc");
        existing.Agent.Should().Be("agent-1");
        existing.GroupName.Should().Be("group-1");
    }

    [Fact]
    public async Task GetOrCreateAsync_IdempotentUnderRace()
    {
        var t1 = _store.GetOrCreateAsync(Ws,
            "a", "a", "a", CancellationToken.None);
        var t2 = _store.GetOrCreateAsync(Ws,
            "b", "b", "b", CancellationToken.None);

        await Task.WhenAll(t1.AsTask(), t2.AsTask());

        var info = await _store.GetAsync(Ws, CancellationToken.None);
        info.Should().NotBeNull();
        // Either "a" or "b" wins — both must succeed without SqliteException leakage.
    }
}
