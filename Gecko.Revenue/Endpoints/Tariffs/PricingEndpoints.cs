using Gecko.Data;
using Gecko.Revenue.Application;
using Gecko.Revenue.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>
/// POST /api/revenue/price — the resolver over HTTP, for the portal, the
/// cashier window and for anyone checking a tariff before approving it.
/// Other modules in the process use <see cref="ITariffPricing"/> directly.
///
/// A POST, not a GET: it is a calculation over a shipment description with a
/// dozen optional fields, not a resource. It changes nothing.
/// </summary>
internal static class PricingEndpoints
{
    public static RouteGroupBuilder MapPricingEndpoints(this RouteGroupBuilder revenue)
    {
        revenue.MapPost("/price", PriceAsync)
            .RequirePermission(RevenuePermissions.TariffView)
            .WithTags("Revenue — pricing")
            .WithSummary("Price one charge for one shipment: which tariff, which row, free time, tiers, surcharges — and why");
        return revenue;
    }

    private static async Task<Results<Ok<PriceResult>, ValidationProblem>> PriceAsync(
        PriceRequest request, ITariffPricing pricing, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(request.ModuleCode)) errors.Add("moduleCode", "Required.");
        if (string.IsNullOrWhiteSpace(request.ChargeCode)) errors.Add("chargeCode", "Required.");
        if (string.IsNullOrWhiteSpace(request.BillTo)) errors.Add("billTo", "Required.");
        if (string.IsNullOrWhiteSpace(request.PaymentTermCode)) errors.Add("paymentTermCode", "Required.");
        if (request.EventTime == default) errors.Add("eventTime", "Required — when the chargeable event happened, with its offset.");
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        try
        {
            return TypedResults.Ok(await pricing.PriceAsync(request, ct));
        }
        catch (TariffPricer.InvalidPriceRequestException e)
        {
            return RevenueSupport.Invalid(e.Field, e.Message);
        }
    }
}
