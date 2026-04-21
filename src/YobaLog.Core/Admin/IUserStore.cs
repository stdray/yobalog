namespace YobaLog.Core.Admin;

// Multi-admin store. Lives in $system.meta.db alongside the workspace catalog. Passwords are
// hashed via AdminPasswordHasher (PBKDF2-HMAC-SHA256, 600k iter, same format as the config
// single-admin fallback). LoginModel consults this store first; if ListAsync is empty it falls
// back to the appsettings "Admin" section — that keeps bootstrap/recovery possible without
// requiring someone to be in the DB to begin with.
public interface IUserStore
{
	ValueTask InitializeAsync(CancellationToken ct);

	ValueTask<IReadOnlyList<UserInfo>> ListAsync(CancellationToken ct);

	// True when the password matches the stored hash for that username; false when either the
	// user doesn't exist or the hash doesn't match. Constant-time at the hash level.
	ValueTask<bool> VerifyAsync(string username, string password, CancellationToken ct);

	// Throws InvalidOperationException if the username already exists.
	ValueTask<UserInfo> CreateAsync(string username, string password, CancellationToken ct);

	// Throws InvalidOperationException if the username doesn't exist.
	ValueTask UpdatePasswordAsync(string username, string newPassword, CancellationToken ct);

	// Returns false when nothing was deleted (unknown username).
	ValueTask<bool> DeleteAsync(string username, CancellationToken ct);
}
