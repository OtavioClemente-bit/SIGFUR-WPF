using System.Security.Cryptography;

namespace SIGFUR.Wpf.Services;

public sealed class PasswordHashResult
{
    public string Hash { get; init; } = string.Empty;
    public string Salt { get; init; } = string.Empty;
    public int Iterations { get; init; } = SecurityService.DefaultIterations;
}

public sealed class SecurityService
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 32;
    private const int HashSize = 32;

    public PasswordHashResult CreatePasswordHash(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Senha obrigatória.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return new PasswordHashResult
        {
            Hash = Convert.ToBase64String(hash),
            Salt = Convert.ToBase64String(salt),
            Iterations = iterations
        };
    }

    public bool VerifyPassword(string password, string storedHash, string storedSalt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(storedSalt))
            return false;
        try
        {
            var salt = Convert.FromBase64String(storedSalt);
            var expected = Convert.FromBase64String(storedHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Max(1, iterations), HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
