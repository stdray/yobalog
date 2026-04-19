using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using YobaLog.Core;
using YobaLog.Core.Sharing;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Sharing;

public sealed class SqliteShareLinkStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteShareLinkStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("share-store");

	public SqliteShareLinkStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-sharestore-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteShareLinkStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync()
	{
		await _store.InitializeWorkspaceAsync(Ws, CancellationToken.None);
	}

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Create_Roundtrips_Everything()
	{
		var expires = DateTimeOffset.UtcNow.AddHours(1);
		var modes = ImmutableDictionary<string, MaskMode>.Empty
			.Add("TraceId", MaskMode.Mask)
			.Add("email", MaskMode.Hide);

		var created = await _store.CreateAsync(Ws, "events | where Level >= 3",
			expires, ["Id", "Timestamp", "TraceId", "email"], modes, CancellationToken.None);

		var fetched = await _store.GetAsync(Ws, created.Id, CancellationToken.None);
		fetched.Should().NotBeNull();
		fetched!.Id.Should().Be(created.Id);
		fetched.Kql.Should().Be("events | where Level >= 3");
		fetched.ExpiresAt.ToUnixTimeMilliseconds().Should().Be(expires.ToUnixTimeMilliseconds());
		fetched.Salt.Length.Should().Be(16);
		fetched.Columns.Should().Equal("Id", "Timestamp", "TraceId", "email");
		fetched.Modes.Should().BeEquivalentTo(modes);
	}

	[Fact]
	public async Task Id_Is_22Char_Base64Url()
	{
		var link = await _store.CreateAsync(Ws, "events",
			DateTimeOffset.UtcNow.AddHours(1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);

		link.Id.Length.Should().Be(22);
		link.Id.Should().MatchRegex("^[A-Za-z0-9_-]{22}$");
		ShortGuid.TryParse(link.Id, out _).Should().BeTrue();
	}

	[Fact]
	public async Task Different_Links_Get_Different_Salts()
	{
		var a = await _store.CreateAsync(Ws, "events",
			DateTimeOffset.UtcNow.AddHours(1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);
		var b = await _store.CreateAsync(Ws, "events",
			DateTimeOffset.UtcNow.AddHours(1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);

		a.Salt.Should().NotEqual(b.Salt);
		a.Id.Should().NotBe(b.Id);
	}

	[Fact]
	public async Task Get_Unknown_Returns_Null()
	{
		var result = await _store.GetAsync(Ws, "ABCDEFGHIJKLMNOPQRSTUV", CancellationToken.None);
		result.Should().BeNull();
	}

	[Fact]
	public async Task Delete_Removes_Link()
	{
		var link = await _store.CreateAsync(Ws, "events",
			DateTimeOffset.UtcNow.AddHours(1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);

		var deleted = await _store.DeleteAsync(Ws, link.Id, CancellationToken.None);
		deleted.Should().BeTrue();

		var fetched = await _store.GetAsync(Ws, link.Id, CancellationToken.None);
		fetched.Should().BeNull();
	}

	[Fact]
	public async Task DeleteExpired_Drops_OnlyPastLinks()
	{
		var now = DateTimeOffset.UtcNow;
		var past = await _store.CreateAsync(Ws, "events",
			now.AddHours(-1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);
		var future = await _store.CreateAsync(Ws, "events",
			now.AddHours(1), [], ImmutableDictionary<string, MaskMode>.Empty, CancellationToken.None);

		var count = await _store.DeleteExpiredAsync(Ws, now, CancellationToken.None);
		count.Should().Be(1);

		(await _store.GetAsync(Ws, past.Id, CancellationToken.None)).Should().BeNull();
		(await _store.GetAsync(Ws, future.Id, CancellationToken.None)).Should().NotBeNull();
	}
}
