using Gecko.Revenue.Application;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>A line's VAT: the total is rounded once and VAT is what is left (owner 2026-10-19, ฿249.99 → ฿250.00).</summary>
public sealed class NetAndTaxTests
{
    [Theory]
    [InlineData("233.6449", "1", "233.64", "16.36")]   // ฿250 incl. VAT, stored to 4 decimals → 250.00
    [InlineData("588.7850", "1", "588.79", "41.21")]   // ฿630 incl. VAT → 630.00
    [InlineData("186.92", "1", "186.92", "13.08")]     // 2-decimal rates bill exactly as before
    [InlineData("654.21", "1", "654.21", "45.79")]
    [InlineData("233.64", "1", "233.64", "16.35")]     // the old 2-decimal rate still gives 249.99
    [InlineData("233.6449", "2", "467.29", "32.71")]   // two boxes → 500.00
    public void Rounds_the_total_once(string rate, string qty, string net, string tax)
    {
        var (n, t) = CashQuoter.NetAndTax(decimal.Parse(rate) * decimal.Parse(qty), 7m);
        Assert.Equal(decimal.Parse(net), n);
        Assert.Equal(decimal.Parse(tax), t);
    }

    [Fact]
    public void No_vat_means_no_tax() => Assert.Equal((233.64m, 0m), CashQuoter.NetAndTax(233.6449m, 0m));
}
