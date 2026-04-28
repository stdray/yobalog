using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Auth;
using YobaLog.Core.Auth.Sqlite;
using YobaLog.Tests.Fakes;

namespace YobaLog.Tests.Auth;

public sealed class SqliteAdminTokenStoreTests : IAsyncLifetime
{
    readonly string _tempDir;
    readonly ServiceProvider _services;
    readonly SqliteAdminTokenStore _store;
    readonly SqliteUserStore _users;

    public SqliteAdminTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yobalog-admintokens-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _services = TestServices.BuildSqliteStores(_tempDir);
        _store = _services.GetRequiredService<SqliteAdminTokenStore>();
        _users = _services.GetRequiredService<SqliteUserStore>();
    }

    public async Task InitializeAsync()
    {
        await _users.InitializeAsync(CancellationToken.None);
        await _store.InitializeAsync(CancellationToken.None);
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
        var created = await _store.CreateAsync("alice", "laptop", CancellationToken.None);

        created.Plaintext.Should().HaveLength(22);
        created.Info.TokenPrefix.Should().HaveLength(6);
        created.Info.TokenPrefix.Should().Be(created.Plaintext[..6]);
        created.Info.Username.Should().Be("alice");
        created.Info.Description.Should().Be("laptop");

        var validation = await _store.ValidateAsync(created.Plaintext, CancellationToken.None);
        validation.Should().BeOfType<AdminTokenValidation.Valid>()
            .Which.Token.Username.Should().Be("alice");
    }

    [Fact]
    public async Task Validate_UnknownToken_Invalid()
    {
        await _store.CreateAsync("alice", "x", CancellationToken.None);
        var r = await _store.ValidateAsync("definitely-not-a-real-token", CancellationToken.None);
        r.Should().BeOfType<AdminTokenValidation.Invalid>();
    }

    [Fact]
    public async Task Validate_MissingToken_Invalid()
    {
        (await _store.ValidateAsync(null, CancellationToken.None)).Should().BeOfType<AdminTokenValidation.Invalid>();
        (await _store.ValidateAsync("", CancellationToken.None)).Should().BeOfType<AdminTokenValidation.Invalid>();
    }

    [Fact]
    public async Task ListByUsername_ReturnsOnlyOwnTokens()
    {
        var a1 = await _store.CreateAsync("alice", "alpha", CancellationToken.None);
        var a2 = await _store.CreateAsync("alice", "beta", CancellationToken.None);
        var b1 = await _store.CreateAsync("bob", "gamma", CancellationToken.None);

        var alice = await _store.ListByUsernameAsync("alice", CancellationToken.None);
        var bob = await _store.ListByUsernameAsync("bob", CancellationToken.None);

        alice.Select(t => t.Id).Should().BeEquivalentTo(new[] { a1.Info.Id, a2.Info.Id });
        bob.Select(t => t.Id).Should().BeEquivalentTo(new[] { b1.Info.Id });
    }

    [Fact]
    public async Task SoftDelete_HidesFromValidate_AndList()
    {
        var created = await _store.CreateAsync("alice", "x", CancellationToken.None);

        (await _store.SoftDeleteAsync(created.Info.Id, CancellationToken.None)).Should().BeTrue();

        (await _store.ValidateAsync(created.Plaintext, CancellationToken.None))
            .Should().BeOfType<AdminTokenValidation.Invalid>();
        (await _store.ListByUsernameAsync("alice", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task SoftDelete_UnknownId_ReturnsFalse()
    {
        (await _store.SoftDeleteAsync(99999, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SoftDelete_AlreadyDeleted_ReturnsFalse()
    {
        var created = await _store.CreateAsync("alice", "x", CancellationToken.None);
        (await _store.SoftDeleteAsync(created.Info.Id, CancellationToken.None)).Should().BeTrue();
        (await _store.SoftDeleteAsync(created.Info.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HardDeleteByUsername_RemovesAllUserTokens()
    {
        var a1 = await _store.CreateAsync("alice", "alpha", CancellationToken.None);
        await _store.CreateAsync("alice", "beta", CancellationToken.None);
        var bob = await _store.CreateAsync("bob", "gamma", CancellationToken.None);

        var removed = await _store.HardDeleteByUsernameAsync("alice", CancellationToken.None);
        removed.Should().Be(2);

        (await _store.ValidateAsync(a1.Plaintext, CancellationToken.None))
            .Should().BeOfType<AdminTokenValidation.Invalid>();
        (await _store.ValidateAsync(bob.Plaintext, CancellationToken.None))
            .Should().BeOfType<AdminTokenValidation.Valid>();
    }

    [Fact]
    public async Task UserDelete_CascadesAdminTokens()
    {
        await _users.CreateAsync("alice", "pw", CancellationToken.None);
        var token = await _store.CreateAsync("alice", "x", CancellationToken.None);

        (await _users.DeleteAsync("alice", CancellationToken.None)).Should().BeTrue();

        (await _store.ValidateAsync(token.Plaintext, CancellationToken.None))
            .Should().BeOfType<AdminTokenValidation.Invalid>();
        (await _store.ListByUsernameAsync("alice", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task PersistsAcrossReinitialize()
    {
        var created = await _store.CreateAsync("alice", "persisted", CancellationToken.None);

        // Simulate process restart — fresh container over the same data directory.
        await using var reopenedServices = TestServices.BuildSqliteStores(_tempDir);
        var reopened = reopenedServices.GetRequiredService<SqliteAdminTokenStore>();
        await reopened.InitializeAsync(CancellationToken.None);

        var r = await reopened.ValidateAsync(created.Plaintext, CancellationToken.None);
        r.Should().BeOfType<AdminTokenValidation.Valid>()
            .Which.Token.Username.Should().Be("alice");
    }
}
