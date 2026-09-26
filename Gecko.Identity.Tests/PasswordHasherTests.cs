using Gecko.Identity.Application.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests;

public class PasswordHasherTests
{
    private const string FixturePassword = "Gecko#Test2026";
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public async Task Verifies_a_real_pbkdf2_fixture_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var system = TestDatabase.ForSystem();
        // Any PBKDF2 row, current or not: rehash-on-login retires them to is_current = 0
        // as fixture users log in, but never deletes them.
        var credential = await system.Credentials
            .Where(c => c.Algorithm == PasswordHasher.Pbkdf2Sha256)
            .FirstAsync(ct);

        Assert.Equal(PasswordHasher.Pbkdf2Sha256, credential.Algorithm);
        Assert.True(_hasher.Verify(FixturePassword, credential.PasswordHash, credential.Algorithm, credential.AlgorithmParams));
        Assert.False(_hasher.Verify("gecko#test2026", credential.PasswordHash, credential.Algorithm, credential.AlgorithmParams));
    }

    [Fact]
    public void Argon2id_round_trips_and_rejects_a_wrong_password()
    {
        var hash = _hasher.Hash(FixturePassword);

        Assert.Equal(PasswordHasher.Argon2Id, hash.Algorithm);
        Assert.True(_hasher.Verify(FixturePassword, hash.Hash, hash.Algorithm, hash.Parameters));
        Assert.False(_hasher.Verify(FixturePassword + "x", hash.Hash, hash.Algorithm, hash.Parameters));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
    {
        Assert.NotEqual(_hasher.Hash(FixturePassword).Hash, _hasher.Hash(FixturePassword).Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_password_is_rejected_not_thrown(string? password)
    {
        var hash = _hasher.Hash(FixturePassword);

        Assert.False(_hasher.Verify(password!, hash.Hash, hash.Algorithm, hash.Parameters));
        _hasher.VerifyDummy(password!);
    }

    [Theory]
    [InlineData("BCRYPT")]
    [InlineData("MD5")]
    public void Unknown_or_unimplemented_algorithm_is_rejected(string algorithm)
    {
        var hash = _hasher.Hash(FixturePassword);

        Assert.False(_hasher.Verify(FixturePassword, hash.Hash, algorithm, hash.Parameters));
    }
}
