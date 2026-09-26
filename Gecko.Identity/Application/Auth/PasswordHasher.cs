using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Gecko.Identity.Application.Auth;

public sealed record PasswordHash(byte[] Hash, string Algorithm, string Parameters);

/// <summary>
/// Verifies whatever iam.credential.algorithm says the hash is, and creates new
/// hashes as Argon2id only.
///
/// WHY TWO ALGORITHMS: SQL Server has no KDF, so the dev fixtures could only be
/// generated as PBKDF2 from script (dev_02). Production passwords are set in C#
/// through invitation acceptance, as Argon2id. Switching on the column — not on
/// the environment — means a re-hash on next login is all a migration needs.
/// </summary>
public sealed class PasswordHasher
{
    public const string Argon2Id = "ARGON2ID";
    public const string Pbkdf2Sha256 = "PBKDF2_SHA256";

    // OWASP Password Storage Cheat Sheet minimum for Argon2id: m=19 MiB, t=2, p=1.
    private const int ArgonMemoryKb = 19_456;
    private const int ArgonIterations = 2;
    private const int ArgonParallelism = 1;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>
    /// Verified on the miss path (unknown email, no credential) so that response
    /// time does not reveal whether an address exists. Email is globally unique,
    /// so without this the login form is an account-enumeration oracle across
    /// every tenant on the platform.
    /// </summary>
    private readonly Lazy<PasswordHash> _dummy;

    public PasswordHasher() => _dummy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    public PasswordHash Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Argon2(password, salt, ArgonMemoryKb, ArgonIterations, ArgonParallelism);

        return new PasswordHash(
            [.. salt, .. key],
            Argon2Id,
            $"m={ArgonMemoryKb},t={ArgonIterations},p={ArgonParallelism};salt=bytes 0-15;dk=bytes 16-47");
    }

    public bool Verify(string password, byte[] hash, string algorithm, string? parameters) => algorithm switch
    {
        // Argon2 throws on an empty password; an input the caller controls must never become a 500.
        _ when string.IsNullOrEmpty(password) => false,
        Pbkdf2Sha256 => VerifyPbkdf2(password, hash, parameters),
        Argon2Id => VerifyArgon2(password, hash, parameters),
        _ => false,   // BCRYPT is allowed by the CHECK constraint but nothing writes it; unknown = reject
    };

    /// <summary>Burns the same work as a real verification, then fails.</summary>
    public void VerifyDummy(string password)
    {
        var dummy = _dummy.Value;
        _ = Verify(password, dummy.Hash, dummy.Algorithm, dummy.Parameters);
    }

    private static bool VerifyPbkdf2(string password, byte[] hash, string? parameters)
    {
        if (hash.Length != SaltBytes + KeyBytes) return false;

        var iterations = ReadInt(parameters, "iterations") ?? 100_000;
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), hash.AsSpan(0, SaltBytes), iterations, HashAlgorithmName.SHA256, KeyBytes);

        return CryptographicOperations.FixedTimeEquals(expected, hash.AsSpan(SaltBytes));
    }

    private static bool VerifyArgon2(string password, byte[] hash, string? parameters)
    {
        if (hash.Length != SaltBytes + KeyBytes) return false;

        var actual = Argon2(
            password,
            hash[..SaltBytes],
            ReadInt(parameters, "m") ?? ArgonMemoryKb,
            ReadInt(parameters, "t") ?? ArgonIterations,
            ReadInt(parameters, "p") ?? ArgonParallelism);

        return CryptographicOperations.FixedTimeEquals(actual, hash.AsSpan(SaltBytes));
    }

    private static byte[] Argon2(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(KeyBytes);
    }

    /// <summary>Reads "name=value" out of "iterations=100000;salt=..." or "m=19456,t=2,p=1;...".</summary>
    private static int? ReadInt(string? parameters, string name)
    {
        if (string.IsNullOrEmpty(parameters)) return null;

        foreach (var part in parameters.Split([';', ','], StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == name && int.TryParse(pair[1], out var value)) return value;
        }
        return null;
    }
}
