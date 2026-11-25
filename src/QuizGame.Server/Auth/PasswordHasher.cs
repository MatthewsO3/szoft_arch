using System.Security.Cryptography;

namespace QuizGame.Server.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashing on the shared framework (no extra package).
/// Stored format: <c>PBKDF2-SHA256$iterations$saltBase64$hashBase64</c>; the iteration count is stored
/// with the hash, so it can be raised later without invalidating existing passwords.
/// </summary>
public sealed class PasswordHasher(int iterations = PasswordHasher.DefaultIterations)
{
    /// <summary>OWASP recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    private const string Prefix = "PBKDF2-SHA256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxAcceptedIterations = 10_000_000; // refuse absurd values from a tampered row

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True only for a well-formed stored hash that matches. Never throws on bad input.</summary>
    public bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var storedIterations) || storedIterations is < 1 or > MaxAcceptedIterations) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length == 0 || expected.Length == 0) return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, storedIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
