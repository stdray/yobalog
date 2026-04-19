using System.Security.Cryptography;

namespace YobaLog.Core.Auth;

public static class AdminPasswordHasher
{
	const int SaltBytes = 16;
	const int KeyBytes = 32;
	const int Iterations = 600_000;
	const string Prefix = "v1";

	public static string Hash(string password)
	{
		ArgumentException.ThrowIfNullOrEmpty(password);
		var salt = RandomNumberGenerator.GetBytes(SaltBytes);
		var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
		return $"{Prefix}:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
	}

	public static bool Verify(string password, string formatted)
	{
		if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(formatted))
			return false;

		var parts = formatted.Split(':');
		if (parts.Length != 4 || parts[0] != Prefix)
			return false;
		if (!int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var iter) || iter <= 0)
			return false;

		byte[] salt;
		byte[] expected;
		try
		{
			salt = Convert.FromBase64String(parts[2]);
			expected = Convert.FromBase64String(parts[3]);
		}
		catch (FormatException)
		{
			return false;
		}

		if (expected.Length != KeyBytes || salt.Length == 0)
			return false;

		var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, KeyBytes);
		return CryptographicOperations.FixedTimeEquals(actual, expected);
	}
}
