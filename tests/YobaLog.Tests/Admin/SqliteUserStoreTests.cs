using Microsoft.Extensions.Options;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Admin;

public sealed class SqliteUserStoreTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly SqliteUserStore _store;

	public SqliteUserStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobalog-users-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_store = new SqliteUserStore(Options.Create(new SqliteLogStoreOptions { DataDirectory = _tempDir }));
	}

	public async Task InitializeAsync() => await _store.InitializeAsync(CancellationToken.None);

	public Task DisposeAsync()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Create_Then_Verify_Accepts_Right_Password()
	{
		await _store.CreateAsync("alice", "correct-horse-battery-staple", CancellationToken.None);

		(await _store.VerifyAsync("alice", "correct-horse-battery-staple", CancellationToken.None)).Should().BeTrue();
		(await _store.VerifyAsync("alice", "wrong", CancellationToken.None)).Should().BeFalse();
	}

	[Fact]
	public async Task Verify_UnknownUser_ReturnsFalse_NotThrows()
	{
		(await _store.VerifyAsync("ghost", "anything", CancellationToken.None)).Should().BeFalse();
	}

	[Fact]
	public async Task Create_DuplicateUsername_Throws()
	{
		await _store.CreateAsync("bob", "pw1", CancellationToken.None);

		await FluentActions.Awaiting(() => _store.CreateAsync("bob", "pw2", CancellationToken.None).AsTask())
			.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*already exists*");
	}

	[Fact]
	public async Task List_Returns_Users_Sorted_By_Username()
	{
		await _store.CreateAsync("charlie", "pw", CancellationToken.None);
		await _store.CreateAsync("alice", "pw", CancellationToken.None);
		await _store.CreateAsync("bob", "pw", CancellationToken.None);

		var users = await _store.ListAsync(CancellationToken.None);
		users.Select(u => u.Username).Should().Equal("alice", "bob", "charlie");
	}

	[Fact]
	public async Task UpdatePassword_Rotates_Accepts_New_Rejects_Old()
	{
		await _store.CreateAsync("dave", "old-pass", CancellationToken.None);
		await _store.UpdatePasswordAsync("dave", "new-pass", CancellationToken.None);

		(await _store.VerifyAsync("dave", "old-pass", CancellationToken.None)).Should().BeFalse();
		(await _store.VerifyAsync("dave", "new-pass", CancellationToken.None)).Should().BeTrue();
	}

	[Fact]
	public async Task UpdatePassword_UnknownUser_Throws()
	{
		await FluentActions.Awaiting(() => _store.UpdatePasswordAsync("nobody", "x", CancellationToken.None).AsTask())
			.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*not found*");
	}

	[Fact]
	public async Task Delete_Removes_User_And_Verify_Fails_After()
	{
		await _store.CreateAsync("eve", "pw", CancellationToken.None);
		(await _store.VerifyAsync("eve", "pw", CancellationToken.None)).Should().BeTrue();

		(await _store.DeleteAsync("eve", CancellationToken.None)).Should().BeTrue();
		(await _store.VerifyAsync("eve", "pw", CancellationToken.None)).Should().BeFalse();
	}

	[Fact]
	public async Task Delete_Unknown_ReturnsFalse()
	{
		(await _store.DeleteAsync("nobody", CancellationToken.None)).Should().BeFalse();
	}
}
