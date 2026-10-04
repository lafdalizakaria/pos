using System.Security.Cryptography;
using System.Text;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Security;

public interface IPinHasher
{
    string Hash(string pin);

    bool Verify(string pin, string encodedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 hasher. Encoded format: <c>PBKDF2-SHA256$iterations$saltBase64$hashBase64</c>.
/// A short PIN has little entropy: lockout (see <see cref="PinLockoutPolicy"/>) is the real protection,
/// and hashes must never leave the server / the register's protected store.
/// </summary>
public sealed class Pbkdf2PinHasher : IPinHasher
{
    public const int DefaultIterations = 600_000;
    private const string Algorithm = "PBKDF2-SHA256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly int _iterations;

    public Pbkdf2PinHasher(int iterations = DefaultIterations)
    {
        if (iterations < 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "At least 1000 iterations are required.");
        }

        _iterations = iterations;
    }

    public static void ValidatePinFormat(string pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
        {
            throw new DomainException("invalid_pin_format", "A PIN must contain 4 to 8 digits.");
        }
    }

    public string Hash(string pin)
    {
        ValidatePinFormat(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, _iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Algorithm}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string pin, string encodedHash)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations) || iterations < 1_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Lockout after repeated PIN failures (defaults: 5 attempts, 15 minutes).</summary>
public sealed record PinLockoutPolicy(int MaxFailedAttempts = 5, TimeSpan? LockoutDuration = null)
{
    public TimeSpan EffectiveLockoutDuration => LockoutDuration ?? TimeSpan.FromMinutes(15);
}
