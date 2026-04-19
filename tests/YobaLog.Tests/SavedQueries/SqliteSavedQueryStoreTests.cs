using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Storage.Sqlite;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace YobaLog.Tests.SavedQueries;

public sealed class SqliteSavedQueryStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteSavedQueryStore _store;
	static readonly WorkspaceId Ws = WorkspaceId.Parse("test-sq");

	public SqliteSavedQueryStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-sq-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteSavedQueryStore(MsOptions.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
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
	public async Task Upsert_Inserts_NewQuery()
	{
		var q = await _store.UpsertAsync(Ws, "errors", "events | where Level >= 4", CancellationToken.None);

		q.Id.Should().BeGreaterThan(0);
		q.Name.Should().Be("errors");
		q.Kql.Should().Be("events | where Level >= 4");
		q.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
		q.UpdatedAt.Should().Be(q.CreatedAt);
	}

	[Fact]
	public async Task Upsert_SameName_UpdatesKql_PreservesCreatedAt()
	{
		var first = await _store.UpsertAsync(Ws, "errors", "events | where Level == 4", CancellationToken.None);
		await Task.Delay(10); // ensure timestamp advances at millisecond resolution
		var second = await _store.UpsertAsync(Ws, "errors", "events | where Level >= 4", CancellationToken.None);

		second.Id.Should().Be(first.Id);
		second.Kql.Should().Be("events | where Level >= 4");
		second.CreatedAt.Should().Be(first.CreatedAt);
		second.UpdatedAt.Should().BeAfter(first.CreatedAt);
	}

	[Fact]
	public async Task Get_ById_RoundTrips()
	{
		var saved = await _store.UpsertAsync(Ws, "q1", "events | take 10", CancellationToken.None);
		var loaded = await _store.GetAsync(Ws, saved.Id, CancellationToken.None);

		loaded.Should().NotBeNull();
		loaded!.Kql.Should().Be("events | take 10");
	}

	[Fact]
	public async Task Get_Unknown_ReturnsNull()
	{
		var loaded = await _store.GetAsync(Ws, 9_999, CancellationToken.None);
		loaded.Should().BeNull();
	}

	[Fact]
	public async Task GetByName_RoundTrips()
	{
		await _store.UpsertAsync(Ws, "by-name", "events | take 1", CancellationToken.None);
		var loaded = await _store.GetByNameAsync(Ws, "by-name", CancellationToken.None);
		loaded.Should().NotBeNull();
		loaded!.Kql.Should().Be("events | take 1");
	}

	[Fact]
	public async Task List_OrderedByName()
	{
		await _store.UpsertAsync(Ws, "beta", "events | take 1", CancellationToken.None);
		await _store.UpsertAsync(Ws, "alpha", "events | take 2", CancellationToken.None);
		await _store.UpsertAsync(Ws, "gamma", "events | take 3", CancellationToken.None);

		var list = await _store.ListAsync(Ws, CancellationToken.None);
		list.Select(q => q.Name).Should().ContainInOrder("alpha", "beta", "gamma");
	}

	[Fact]
	public async Task Delete_RemovesById()
	{
		var saved = await _store.UpsertAsync(Ws, "to-delete", "events", CancellationToken.None);
		var deleted = await _store.DeleteAsync(Ws, saved.Id, CancellationToken.None);
		deleted.Should().BeTrue();

		(await _store.GetAsync(Ws, saved.Id, CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task Delete_Unknown_ReturnsFalse()
	{
		var deleted = await _store.DeleteAsync(Ws, 9_999, CancellationToken.None);
		deleted.Should().BeFalse();
	}

	[Fact]
	public async Task WorkspaceIsolation_DataDoesNotLeak()
	{
		var otherWs = WorkspaceId.Parse("other-sq");
		await _store.InitializeWorkspaceAsync(otherWs, CancellationToken.None);

		await _store.UpsertAsync(Ws, "in-main", "events", CancellationToken.None);
		await _store.UpsertAsync(otherWs, "in-other", "events", CancellationToken.None);

		var mainList = await _store.ListAsync(Ws, CancellationToken.None);
		var otherList = await _store.ListAsync(otherWs, CancellationToken.None);

		mainList.Single().Name.Should().Be("in-main");
		otherList.Single().Name.Should().Be("in-other");
	}

	[Fact]
	public async Task Drop_RemovesMetaFile()
	{
		var tempWs = WorkspaceId.Parse("temp-sq-drop");
		await _store.InitializeWorkspaceAsync(tempWs, CancellationToken.None);
		await _store.UpsertAsync(tempWs, "x", "events", CancellationToken.None);

		await _store.DropWorkspaceAsync(tempWs, CancellationToken.None);

		File.Exists(Path.Combine(_tempDir, "temp-sq-drop.meta.db")).Should().BeFalse();
	}
}
