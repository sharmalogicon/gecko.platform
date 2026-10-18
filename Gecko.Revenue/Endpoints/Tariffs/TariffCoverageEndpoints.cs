using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>One charge of an order type: what each box type pays, and the box types no tariff prices.</summary>
public sealed record CoverageChargeResponse(
    string ChargeCode, string? MovementCode, string BillTo, string PaymentTermCode, bool IsValueAddedService,
    IReadOnlyList<CoveragePriceResponse> Priced, IReadOnlyList<string> Unpriced);

public sealed record CoveragePriceResponse(string EquipmentTypeCode, decimal? Amount, string? ScheduleNo);

public sealed record TariffCoverageResponse(
    string OrderTypeCode, string? CustomerCode, DateTimeOffset At, int Charges, int Gaps, IReadOnlyList<CoverageChargeResponse> Rows);

/// <summary>
/// Tariff coverage (owner 2026-10-18): for each charge of an order type and each active box type, what the gate would
/// price — asked of the pricer itself, so it follows the same precedence (booking, contract, standard; type before size
/// before any). An UNPRICED pair is a box that would pass the gate uncharged (a cash line with no rate is skipped, owner
/// 2026-10-07): the screen shows them while the tariff is being set up instead of after the move.
/// </summary>
internal static class TariffCoverageEndpoints
{
    public static async Task<Results<Ok<TariffCoverageResponse>, ValidationProblem>> CoverageAsync(
        IMasterDataReferences master, ITariffPricing pricer, TimeProvider time, CancellationToken ct,
        string? orderTypeCode = null, Guid? branchId = null, string? customerCode = null, string? paymentTermCode = null, DateTimeOffset? at = null,
        string? truckCategoryCode = null, string? cargoCategoryCode = null, string? agentCode = null, string? forwarderCode = null)
    {
        var orderType = orderTypeCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(orderType)) return RevenueSupport.Invalid("orderTypeCode", "Which order type? Its code.");
        // A depot's own tariffs (the standard one is usually branch-scoped) only apply with the branch named.
        if (branchId is null) return RevenueSupport.Invalid("branchId", "Which depot? Its tariffs apply only there.");
        var customer = string.IsNullOrWhiteSpace(customerCode) ? null : customerCode.Trim().ToUpperInvariant();
        var defaultTerm = string.IsNullOrWhiteSpace(paymentTermCode) ? null : paymentTermCode.Trim().ToUpperInvariant();
        var when = at ?? time.GetUtcNow();

        var charges = await master.OrderTypeChargesAsync(orderType, ct);
        var types = await master.ActiveEquipmentTypesAsync(ct);
        var rows = new List<CoverageChargeResponse>();
        foreach (var charge in charges.OrderBy(c => c.MovementCode).ThenBy(c => c.ChargeCode))
        {
            var term = charge.PaymentTermCode ?? defaultTerm ?? "CASH";
            var priced = new List<CoveragePriceResponse>();
            var unpriced = new List<string>();
            foreach (var type in types.OrderBy(t => t.SizeCode).ThenBy(t => t.TypeCode))
            {
                PriceResult? result = null;
                try
                {
                    result = await pricer.PriceAsync(new PriceRequest(
                        ModuleCode: "TOS", BranchId: branchId, EventTime: when, ChargeCode: charge.ChargeCode, BillTo: charge.BillTo,
                        PaymentTermCode: term, CustomerPartyCode: customer,
                        AgentPartyCode: agentCode?.Trim().ToUpperInvariant(), ForwarderPartyCode: forwarderCode?.Trim().ToUpperInvariant(), OrderTypeCode: orderType, MovementCode: charge.MovementCode,
                        EquipmentTypeCode: type.TypeCode, Quantity: 1,
                        TruckCategoryCode: truckCategoryCode?.Trim().ToUpperInvariant(), CargoCategoryCode: cargoCategoryCode?.Trim().ToUpperInvariant()), ct);
                }
                catch (TariffPricer.InvalidPriceRequestException) { }
                if (result is { Outcome: PriceOutcomes.Priced }) priced.Add(new CoveragePriceResponse(type.TypeCode, result.Amount, result.ScheduleNo));
                else unpriced.Add(type.TypeCode);
            }
            rows.Add(new CoverageChargeResponse(charge.ChargeCode, charge.MovementCode, charge.BillTo, term, charge.IsValueAddedService, priced, unpriced));
        }
        return TypedResults.Ok(new TariffCoverageResponse(orderType, customer, when, rows.Count, rows.Sum(r => r.Unpriced.Count), rows));
    }
}
