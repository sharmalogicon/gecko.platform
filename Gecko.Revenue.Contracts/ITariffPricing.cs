namespace Gecko.Revenue.Contracts;

/// <summary>
/// "What does this cost?" — the Revenue context's answer, for any module that
/// needs a price at a HUMAN moment: the portal, the cashier window, the nightly
/// accrual. NEVER the barrier: ADR-007's gate coupon carries the price there.
///
/// The result is a SNAPSHOT (ROADMAP decision 4). A caller stores what comes
/// back — rate, schedule, version, row version, trail — and never asks again for
/// the same event: a tariff revised in November must not change what an October
/// gate move cost.
///
/// UNPRICED is an answer, not an error. The caller decides: the gate refuses a
/// coupon, the accrual job parks the line for review. Nothing here guesses.
/// </summary>
public interface ITariffPricing
{
    Task<PriceResult> PriceAsync(PriceRequest request, CancellationToken ct);
}

/// <param name="EventTime">When the chargeable thing happened. Converted to the BRANCH's calendar date before any tariff dates are compared.</param>
/// <param name="Quantity">
/// FLAT: how many billing units (containers, documents…), default 1.
/// Tiered by DAY / HOUR: the RAW elapsed units — free time is removed here, not by the caller.
/// Tiered by TEU / FLEET_TEU: the TEU count.
/// </param>
/// <param name="FreeTimeKind">STORAGE / CHASSIS / TRUCK_WAITING — which free-time rules apply. Null = none.</param>
/// <param name="IsDangerousGoods">The SHIPMENT's hazard status; drives IS_DG surcharges and the DG free-time group.</param>
public sealed record PriceRequest(
    string ModuleCode,
    Guid? BranchId,
    DateTimeOffset EventTime,
    string ChargeCode,
    string BillTo,
    string PaymentTermCode,
    string? AgentPartyCode = null,
    string? ForwarderPartyCode = null,
    string? CustomerPartyCode = null,
    string? BookingRef = null,
    string? OrderTypeCode = null,
    string? MovementCode = null,
    string? EquipmentTypeCode = null,
    string? EquipmentSize = null,
    string? CargoCategoryCode = null,
    string? TruckCategoryCode = null,
    bool IsDangerousGoods = false,
    decimal? GrossWeightKg = null,
    decimal Quantity = 1,
    string? FreeTimeKind = null,
    string? FullEmpty = null,
    string? Direction = null);

public static class PriceOutcomes
{
    public const string Priced = "PRICED";
    public const string Unpriced = "UNPRICED";
}

public sealed record PricedTier(decimal FromQty, decimal? ToQty, decimal Quantity, decimal BaseRate, decimal Rate, decimal Amount);

/// <param name="Before">Unit rate before this modifier.</param>
public sealed record AppliedCondition(short SequenceNo, string Label, decimal Before, decimal After);

public sealed record PriceResult(
    string Outcome,
    string ChargeCode,
    string BillTo,
    string PaymentTermCode,
    DateOnly PricedForDate,
    Guid? ScheduleId,
    string? ScheduleNo,
    short? VersionNo,
    string? ScheduleType,
    byte? ScopeRank,
    Guid? TosRateId,
    string? RateRowVersion,
    int? Specificity,
    string? PricingMethod,
    string? BillingUnitCode,
    string? CurrencyCode,
    bool? PricesIncludeTax,
    decimal? BaseRate,
    decimal? UnitRate,
    decimal Quantity,
    decimal? FreeUnits,
    string? FreeTimeFromScheduleNo,
    decimal? ChargeableQuantity,
    IReadOnlyList<PricedTier> Tiers,
    IReadOnlyList<AppliedCondition> Conditions,
    decimal? Amount,
    IReadOnlyList<string> PrecedenceTrail,
    DateTimeOffset ResolvedAt);
