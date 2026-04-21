using Microsoft.Extensions.Options;
using YobaLog.Core.Retention;
using YobaLog.Core.Retention.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Retention;

public sealed class SqliteRetentionPolicyStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteRetentionPolicyStore _store;
	static readonly WorkspaceId WsA = WorkspaceId.Parse("rp-a");
	static readonly WorkspaceId WsB = WorkspaceId.Parse("rp-b");

	public SqliteRetentionPolicyStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-retpol-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteRetentionPolicyStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync() => await _store.InitializeAsync(CancellationToken.None);

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Upsert_Then_ListByWorkspace_Returns_Policy()
	{
		await _store.UpsertAsync(
			new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "errors-only", RetainDays = 30 },
			CancellationToken.None);

		var policies = await _store.ListByWorkspaceAsync(WsA, CancellationToken.None);
		policies.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "errors-only", RetainDays = 30 });
	}

	[Fact]
	public async Task Upsert_Same_Key_Replaces_RetainDays()
	{
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "warn", RetainDays = 30 }, CancellationToken.None);
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "warn", RetainDays = 60 }, CancellationToken.None);

		var policies = await _store.ListByWorkspaceAsync(WsA, CancellationToken.None);
		policies.Should().ContainSingle().Which.RetainDays.Should().Be(60);
	}

	[Fact]
	public async Task ListByWorkspace_Filters_Per_Workspace()
	{
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "sqA", RetainDays = 7 }, CancellationToken.None);
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsB.Value, SavedQuery = "sqB", RetainDays = 14 }, CancellationToken.None);

		(await _store.ListByWorkspaceAsync(WsA, CancellationToken.None)).Select(p => p.SavedQuery).Should().Equal("sqA");
		(await _store.ListByWorkspaceAsync(WsB, CancellationToken.None)).Select(p => p.SavedQuery).Should().Equal("sqB");
	}

	[Fact]
	public async Task List_Returns_All_Sorted_By_Workspace_Then_SavedQuery()
	{
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsB.Value, SavedQuery = "zeta", RetainDays = 1 }, CancellationToken.None);
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "alpha", RetainDays = 7 }, CancellationToken.None);
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "beta", RetainDays = 14 }, CancellationToken.None);

		var all = await _store.ListAsync(CancellationToken.None);
		all.Select(p => (p.Workspace, p.SavedQuery)).Should().Equal(
			(WsA.Value, "alpha"),
			(WsA.Value, "beta"),
			(WsB.Value, "zeta"));
	}

	[Fact]
	public async Task Delete_Removes_Row_ReturnsTrue()
	{
		await _store.UpsertAsync(new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "go", RetainDays = 7 }, CancellationToken.None);

		(await _store.DeleteAsync(WsA, "go", CancellationToken.None)).Should().BeTrue();
		(await _store.ListByWorkspaceAsync(WsA, CancellationToken.None)).Should().BeEmpty();
	}

	[Fact]
	public async Task Delete_Unknown_ReturnsFalse()
	{
		(await _store.DeleteAsync(WsA, "nope", CancellationToken.None)).Should().BeFalse();
	}

	[Fact]
	public async Task Upsert_Rejects_Invalid_RetainDays()
	{
		await FluentActions.Awaiting(() => _store.UpsertAsync(
			new RetentionPolicy { Workspace = WsA.Value, SavedQuery = "x", RetainDays = 0 }, CancellationToken.None).AsTask())
			.Should().ThrowAsync<ArgumentException>();
	}
}
