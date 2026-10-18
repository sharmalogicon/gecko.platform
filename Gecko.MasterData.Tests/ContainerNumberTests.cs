using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Tests;

/// <summary>
/// ISO 6346, checked two ways: against the published example and against the
/// SQL function, on real production numbers.
/// </summary>
public sealed class ContainerNumberTests
{
    [Theory]
    // The example from the standard itself.
    [InlineData("CSQU3054383")]
    // Real numbers from Vector's registry (dev_04), across owner codes and types.
    [InlineData("AMFU8539517")]
    [InlineData("AKLU6006714")]
    [InlineData("AMCU9295585")]
    [InlineData("APZU4230891")]
    [InlineData("BMOU8000544")]
    [InlineData("BGBU5010740")]
    [InlineData("APHU4625522")]
    // Check digit 0, which is the case a mod-11 implementation gets wrong: a
    // remainder of 10 is written as 0, not dropped and not rejected.
    [InlineData("AMCU9301820")]
    [InlineData("AKLU6022680")]
    public void Real_container_numbers_are_valid(string containerNo)
    {
        Assert.True(ContainerNumber.IsWellFormed(containerNo));
        Assert.True(ContainerNumber.IsValid(containerNo), $"{containerNo} should be a valid ISO 6346 number");
    }

    [Theory]
    [InlineData("CSQU3054383", 3)]
    [InlineData("AMCU9301820", 0)]
    [InlineData("AKLU6006714", 4)]
    public void Check_digit_matches_the_published_algorithm(string containerNo, int expected) =>
        Assert.Equal(expected, ContainerNumber.CheckDigitOf(containerNo));

    [Theory]
    [InlineData("CSQU3054384")]   // last digit off by one
    [InlineData("CSQU3054380")]
    [InlineData("AMFU8539510")]
    public void A_wrong_check_digit_is_rejected(string containerNo)
    {
        Assert.True(ContainerNumber.IsWellFormed(containerNo));   // shape is fine
        Assert.False(ContainerNumber.IsValid(containerNo));       // the number is not
    }

    /// <summary>
    /// Owner 2026-10-08: no ISO checks on entry — some real boxes do not follow ISO 6346
    /// (TMSKORAKIT: ANVY6399011, PLD9256601, RBI051, 4043535). Any 4–11 letters or digits is
    /// accepted; it is simply not an ISO number, so it has no check digit to pass.
    /// </summary>
    [Theory]
    [InlineData("CSQ3054383")]     // three owner letters
    [InlineData("CSQUA054383")]    // letter in the serial
    [InlineData("CSQX3054383")]    // X is not an ISO equipment category (U, J, Z only)
    [InlineData("CSQU305438")]     // ten characters
    [InlineData("PLD9256601")]
    [InlineData("RBI051")]
    [InlineData("4043535")]
    public void Non_iso_numbers_are_accepted_but_not_iso_valid(string containerNo)
    {
        Assert.True(ContainerNumber.IsWellFormed(containerNo));
        Assert.False(ContainerNumber.IsIsoFormed(containerNo));
        Assert.False(ContainerNumber.IsValid(containerNo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC")]            // fewer than 4 characters
    [InlineData("CSQU30543833")]   // more than 11
    [InlineData("CSQU-3054383")]   // punctuation (Normalise strips it first)
    public void Malformed_numbers_are_rejected(string? containerNo)
    {
        Assert.False(ContainerNumber.IsWellFormed(containerNo));
        Assert.False(ContainerNumber.IsValid(containerNo));
    }

    [Theory]
    [InlineData("csqu3054383", "CSQU3054383")]
    [InlineData("CSQU 305438 3", "CSQU3054383")]
    [InlineData("CSQU-3054383", "CSQU3054383")]
    [InlineData("  CSQU3054383  ", "CSQU3054383")]
    public void Normalise_accepts_what_humans_and_scanners_actually_produce(string input, string expected)
    {
        Assert.Equal(expected, ContainerNumber.Normalise(input));
        Assert.True(ContainerNumber.IsValid(ContainerNumber.Normalise(input)));
    }

    [Fact]
    public void Prefix_is_the_owner_code_plus_category() =>
        Assert.Equal("CSQU", ContainerNumber.PrefixOf("CSQU3054383"));

    /// <summary>
    /// The C# implementation and dbo.fn_container_check_digit must never disagree.
    /// The gate validates in C# for speed; fixtures and the Vector ETL validate in
    /// SQL in bulk. If they drift, one of them starts turning away real boxes —
    /// and this test is checked against the whole registry, not a sample.
    /// </summary>
    [Fact]
    public async Task Csharp_and_sql_agree_on_every_container_in_the_registry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);

        var numbers = await db.Containers.AsNoTracking().Select(c => c.ContainerNo).ToListAsync(ct);
        Assert.NotEmpty(numbers);

        foreach (var number in numbers)
        {
            var fromSql = await db.Database
                .SqlQuery<byte>($"SELECT dbo.fn_container_check_digit({number}) AS Value")   // TINYINT
                .SingleAsync(ct);

            Assert.Equal(fromSql, ContainerNumber.CheckDigitOf(number));
            Assert.True(ContainerNumber.IsValid(number), $"{number} is in the registry but does not validate");
        }
    }
}
