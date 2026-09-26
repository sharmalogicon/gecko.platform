using System.Net;
using System.Net.Http.Json;
using Gecko.Revenue.Endpoints.Tariffs;

namespace Gecko.Revenue.Tests.Api;

/// <summary>GET /api/revenue/lookups — the vocabularies RateSetValidator checks a rate row against.</summary>
[Collection(RevenueApiCollection.Name)]
public sealed class LookupApiTests(RevenueApiFactory api)
{
    private const string Lookups = "/api/revenue/lookups";

    [Fact]
    public async Task Lookups_list_the_codes_a_rate_row_may_use_with_their_flags()
    {
        var ct = TestContext.Current.CancellationToken;
        var accounts = await api.ClientForAsync(RevenueApiFactory.SctAccounts);

        var lookups = await accounts.GetFromJsonAsync<RevenueLookupsResponse>(Lookups, ct);

        Assert.NotNull(lookups);
        Assert.Contains(lookups.BillToRoles, b => b.Code == "CUSTOMER" && !string.IsNullOrEmpty(b.Name));
        Assert.Contains(lookups.PaymentTerms, p => p.Code == "CASH" && p.SettlesBeforeRelease && !p.RequiresCreditAccount);
        Assert.Contains(lookups.PaymentTerms, p => p.Code == "CREDIT" && !p.SettlesBeforeRelease);
        Assert.Contains(lookups.BillingUnits, u => u.Code == "PER_DAY" && u.IsTimeBased && u.QuantitySource == "DAY");
        Assert.Contains(lookups.BillingUnits, u => u.Code == "PER_CONTAINER" && !u.IsTimeBased);
        Assert.Contains(lookups.Currencies, c => c.Code == "THB");

        // In display order, as the drop-downs show them.
        Assert.Equal(lookups.BillingUnits.OrderBy(u => u.SortOrder).Select(u => u.Code), lookups.BillingUnits.Select(u => u.Code));
    }

    [Fact]
    public async Task A_caller_without_tariff_view_cannot_read_them() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await api.ClientForAsync(RevenueApiFactory.SctOpsLcb)).GetAsync(Lookups, TestContext.Current.CancellationToken)).StatusCode);
}
