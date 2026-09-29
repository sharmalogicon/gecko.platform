namespace Gecko.MasterData.Contracts;

/// <summary>
/// How another module checks the master-data codes it stores — the only way,
/// since there are no cross-database foreign keys and no cross-module EF.
///
/// Everything is looked up BY CODE, because that is what a user types and what
/// an Excel sheet carries, and it answers with the id AND the code: the caller
/// stores both (ROADMAP decision 3). Scoped to the current request's tenant by
/// the MasterData module's own RLS-bound connection, so a code from another
/// tenant is simply "not found".
///
/// Admin-time only (tariff editing, imports). The gate barrier must never call
/// this — ADR-007's coupon pattern keeps the 3-second path local.
/// </summary>
public interface IMasterDataReferences
{
    Task<IReadOnlyDictionary<string, BranchRef>> BranchesAsync(IEnumerable<Guid> branchIds, CancellationToken ct);
    Task<IReadOnlyDictionary<string, PartyRef>> PartiesAsync(IEnumerable<string> partyCodes, CancellationToken ct);
    /// <summary>Every active charge code of a module, for template drop-downs.</summary>
    Task<IReadOnlyList<string>> ChargeCodesForModuleAsync(string moduleCode, CancellationToken ct);

    // ── "list all" — for template drop-downs (the user picks, never types) ──

    /// <summary>Every active order type code of the tenant, sorted.</summary>
    Task<IReadOnlyList<string>> ActiveOrderTypeCodesAsync(CancellationToken ct);

    /// <summary>Every active movement code that applies to a module (the module's own, plus BOTH), sorted.</summary>
    Task<IReadOnlyList<string>> MovementCodesForModuleAsync(string moduleCode, CancellationToken ct);

    /// <summary>Every active equipment type of the tenant, sorted by length then code.</summary>
    Task<IReadOnlyList<EquipmentTypeRef>> ActiveEquipmentTypesAsync(CancellationToken ct);

    /// <summary>Every active value of a code-list category for this tenant (global or tenant-defined), in sort order.</summary>
    Task<IReadOnlyList<string>> CodeListAsync(string categoryCode, CancellationToken ct);

    Task<IReadOnlyDictionary<string, ChargeCodeRef>> ChargeCodesAsync(IEnumerable<string> chargeCodes, CancellationToken ct);
    Task<IReadOnlyDictionary<string, CodeRef>> OrderTypesAsync(IEnumerable<string> orderTypeCodes, CancellationToken ct);
    Task<IReadOnlyDictionary<string, CodeRef>> MovementsAsync(IEnumerable<string> movementCodes, CancellationToken ct);
    Task<IReadOnlyDictionary<string, EquipmentTypeRef>> EquipmentTypesAsync(IEnumerable<string> typeCodes, CancellationToken ct);

    /// <summary>The subset of <paramref name="codes"/> that are active values of the category for this tenant (global or tenant-defined).</summary>
    Task<IReadOnlySet<string>> CodeListValuesAsync(string categoryCode, IEnumerable<string> codes, CancellationToken ct);

    /// <summary>A BOOL tenant setting, resolved branch → tenant → declared default.</summary>
    Task<bool> GetBoolSettingAsync(string settingKey, Guid? branchId, bool fallback, CancellationToken ct);

    /// <summary>An INT tenant setting, resolved branch → tenant → declared default.</summary>
    Task<int> GetIntSettingAsync(string settingKey, Guid? branchId, int fallback, CancellationToken ct);

    // ── TOS (gecko_tos PLAN P-3) ─────────────────────────────────────────────
    // Vessel calls point at a vessel, a port and a terminal. Holds, conditions,
    // grades and order-type plans arrive with the TOS batches that need them.

    Task<IReadOnlyDictionary<string, VesselRef>> VesselsAsync(IEnumerable<string> vesselCodes, CancellationToken ct);
    Task<IReadOnlyDictionary<string, PortRef>> PortsAsync(IEnumerable<string> portCodes, CancellationToken ct);

    /// <summary>Locations of type TERMINAL only — a depot or a warehouse is not a berth.</summary>
    Task<IReadOnlyDictionary<string, CodeRef>> TerminalsAsync(IEnumerable<string> terminalCodes, CancellationToken ct);

    /// <summary>
    /// Order types WITH their step list and each step's gate rules — the plan a
    /// booking snapshots onto every box it assigns (gecko_tos PLAN §4.2). Steps
    /// are in sequence order.
    /// </summary>
    Task<IReadOnlyDictionary<string, OrderTypePlanRef>> OrderTypePlansAsync(IEnumerable<string> orderTypeCodes, CancellationToken ct);

    /// <summary>The container registry, by ISO 6346 number. A number not in the registry is simply absent.</summary>
    Task<IReadOnlyDictionary<string, ContainerRef>> ContainersAsync(IEnumerable<string> containerNos, CancellationToken ct);

    Task<IReadOnlyDictionary<string, CommodityRef>> CommoditiesAsync(IEnumerable<string> commodityCodes, CancellationToken ct);

    Task<IReadOnlyDictionary<string, CodeRef>> ContainerGradesAsync(IEnumerable<string> gradeCodes, CancellationToken ct);

    /// <summary>
    /// Hold types WITH what they block and who may lift them. TOS stores the id and
    /// the code on every hold row, but the decision at release time — and at the
    /// barrier — is made from <see cref="HoldRef.ReleaseAuthority"/> and
    /// <see cref="HoldRef.BlockingScope"/>, which live here (gecko_tos PLAN §4.3).
    /// A hold type deleted in master data still resolves, with IsActive = false:
    /// the boxes that carry it stay held and can still be released.
    /// </summary>
    Task<IReadOnlyDictionary<string, HoldRef>> HoldsAsync(IEnumerable<string> holdCodes, CancellationToken ct);

    /// <summary>Every active hold type of the tenant — for the apply drop-down.</summary>
    Task<IReadOnlyList<HoldRef>> ActiveHoldsAsync(CancellationToken ct);

    /// <summary>
    /// The CEDEX vocabulary a gate survey is written in: damage codes (with whether
    /// each one makes the box unserviceable), components and locations. One call,
    /// because a survey validates all three at once (gecko_tos PLAN §4.4).
    /// </summary>
    Task<SurveyCodeSets> SurveyCodesAsync(CancellationToken ct);

    // ── Revenue billing (PLAN_BILLING §4.1) ──────────────────────────────────

    /// <summary>
    /// What an order type CAN charge (Vector <c>Master.OrderTypeCharges</c>): per
    /// movement, or order-level VAS when <see cref="OrderTypeChargeRef.MovementCode"/>
    /// is null. Who pays and how is the tariff's decision — this is only the menu.
    /// </summary>
    Task<IReadOnlyList<OrderTypeChargeRef>> OrderTypeChargesAsync(string orderTypeCode, CancellationToken ct);

    /// <summary>The bill-to × term variants of these charge codes, each with its tax (the cash receipt is a tax invoice).</summary>
    Task<IReadOnlyList<ChargeVariantRef>> ChargeVariantsAsync(IEnumerable<string> chargeCodes, CancellationToken ct);

    /// <summary>Every active variant of every active charge code of a type — STORAGE, which no movement raises.</summary>
    Task<IReadOnlyList<ChargeVariantRef>> ChargeVariantsOfTypeAsync(string chargeType, CancellationToken ct);

    /// <summary>
    /// The legal entity that issues tax invoices at a branch (org.branch_profile.company_id
    /// → org.company): the seller block of a Thai receipt / tax invoice. Null when the
    /// branch has no profile or no company. A field MDM does not hold is null — never guessed.
    /// </summary>
    Task<InvoicingCompanyRef?> InvoicingCompanyAsync(Guid branchId, CancellationToken ct);

    /// <summary>
    /// Stated capacity of a branch's active yards (org.yard.capacity_teu), summed.
    /// Null when no active yard records a capacity — "unknown", not zero.
    /// </summary>
    Task<int?> YardCapacityTeuAsync(Guid branchId, CancellationToken ct);
}

/// <summary>
/// The seller on a tax invoice. <see cref="TaxBranchNo"/> and the address are the
/// ISSUING BRANCH's when the branch records its own (Revenue Code s.86/4: the
/// place of business that issues it), else the company's.
/// </summary>
/// <param name="TaxBranchNo">Thai 5-digit tax branch: "00000" = head office (สำนักงานใหญ่).</param>
/// <param name="IsHeadOffice">Null when the tax branch is not recorded.</param>
/// <param name="Address">One line, the parts MDM holds joined with ", "; null when it holds none.</param>
public sealed record InvoicingCompanyRef(
    Guid CompanyId, string CompanyCode, string LegalNameEn, string? LegalNameLocal,
    string? TaxId, string? TaxBranchNo, bool? IsHeadOffice,
    string? Address, string? Phone, string? Email);

/// <param name="PaymentTermCode">Null = both CASH and CREDIT variants apply (Vector's CA/CR pair collapsed to one row).</param>
public sealed record OrderTypeChargeRef(
    Guid ChargeCodeId, string ChargeCode, string? MovementCode, string BillTo, string? PaymentTermCode,
    bool IsDefault, bool IsOptional, bool IsValueAddedService, bool RaiseAtGateIn, decimal? DefaultQty);

/// <param name="ChargeCategory">For STORAGE: EMPTY or LADEN — which stays it applies to.</param>
public sealed record ChargeVariantRef(
    Guid ChargeCodeId, string ChargeCode, string DescriptionEn, string? DescriptionLocal,
    string ChargeType, string ChargeCategory, string BillingUnitCode,
    string BillTo, string PaymentTermCode, string? TaxCode, decimal TaxRatePct, short? CreditTermDays);

public sealed record SurveyCodeSets(
    IReadOnlyDictionary<string, DamageCodeRef> DamageCodes,
    IReadOnlySet<string> Components,
    IReadOnlySet<string> Locations);

/// <summary><see cref="MakesUnserviceable"/> is what turns a survey into a hold, not the surveyor's mood.</summary>
public sealed record DamageCodeRef(string DamageCode, string DescriptionEn, byte Severity, bool MakesUnserviceable);

/// <summary>Dictionary keys are the CODE, upper-case (for branches, the id as a string).</summary>
public sealed record CodeRef(Guid Id, string Code, bool IsActive);

public sealed record BranchRef(Guid BranchId, string BranchCode, string? TimeZone);

public sealed record PartyRef(Guid PartyId, string PartyCode, string Name, bool IsActive,
    bool IsCustomer, bool IsShippingLine, bool IsForwarder, bool IsHaulier);

public sealed record ChargeCodeRef(Guid ChargeCodeId, string ChargeCode, string ModuleCode, string BillingUnitCode, bool IsActive);

/// <summary><see cref="SizeCode"/> is the length in feet as the tariffs write it: "20", "40", "45".</summary>
public sealed record EquipmentTypeRef(Guid EquipmentTypeId, string TypeCode, string SizeCode, bool IsReefer, bool IsOog, bool IsActive, decimal Teu);

public sealed record VesselRef(Guid VesselId, string VesselCode, string VesselName, string? ImoNumber, bool IsActive);

public sealed record PortRef(Guid PortId, string PortCode, string Name, string? UnLocode, string? TimeZone, bool IsActive);

public sealed record OrderTypePlanRef(
    Guid OrderTypeId, string OrderTypeCode, bool IsActive,
    string DirectionCode, string CargoClassCode, string? BookingTypeCode,
    IReadOnlyList<OrderTypeStepRef> Steps)
{
    /// <summary>Any step demands a vessel call — the booking must then name one.</summary>
    public bool RequiresVesselCall => Steps.Any(s => s.RequireVesselVoyage);
}

/// <summary>
/// One step, the five gate rules it enforces (MDM commercial.order_type_movement)
/// and what its movement IS (<c>commercial.movement</c>): which way the box goes and
/// whether it is laden. The barrier validates the transaction against those two
/// rather than trusting what the clerk picked (gecko_tos PLAN §5.3).
/// </summary>
public sealed record OrderTypeStepRef(
    Guid OrderTypeMovementId, Guid MovementId, string MovementCode, short SequenceNo, bool IsRequired, bool IsBillable,
    bool CheckSealNo, bool CheckGrossWeight, bool RequireVesselVoyage, bool AllowDamagedRelease, bool SkipEdi,
    string Direction, string FullEmpty, bool RequiresSurvey, bool ChangesYardPosition);

/// <summary><see cref="EquipmentTypeCode"/> is null when the registry row was entered without a type.</summary>
public sealed record ContainerRef(Guid ContainerId, string ContainerNo, string? EquipmentTypeCode, string Status, bool IsCheckDigitValid);

/// <summary>
/// <see cref="BlockingScope"/> is ALL / RELEASE / LOAD / GATE_IN / GATE_OUT and
/// <see cref="ReleaseAuthority"/> SUPERVISOR / MNR / DEPOT_OPERATIONS /
/// DEPOT_FINANCE / LINE / CUSTOMS (gecko_master equipment.hold).
/// </summary>
public sealed record HoldRef(
    Guid HoldId, string HoldCode, string DescriptionEn, string HoldType,
    string BlockingScope, string ReleaseAuthority, byte Priority, string? DisplayColorHex, bool IsActive,
    string? AutoApplyOnEvent);

public sealed record CommodityRef(Guid CommodityId, string CommodityCode, bool IsDangerous, bool IsTemperatureControlled, bool IsActive);

/// <summary>Setting keys owned by other modules but stored in master data.</summary>
public static class RevenueSettingKeys
{
    /// <summary>gecko_master 16_revenue_settings.sql — relaxes tariff maker-checker.</summary>
    public const string TariffSelfApprovalAllowed = "revenue.tariff_self_approval_allowed";
}

/// <summary>gecko_master 17_tos_prerequisites.sql.</summary>
public static class TosSettingKeys
{
    /// <summary>Hours before the port cut-off that a depot stops receiving, when a call has no YARD cut-off (PLAN Q3).</summary>
    public const string YardCutoffLeadHours = "tos.yard_cutoff_lead_hours";

    public const string WalkInBookingAllowed = "tos.walk_in_booking_allowed";

    /// <summary>gecko_master 12_seed_config_definitions.sql — a box not in the registry may still be booked / gated.</summary>
    public const string AllowUnknownContainer = "gate.allow_unknown_container";

    /// <summary>gecko_master 12_seed_config_definitions.sql — refuse a container number whose ISO 6346 check digit fails.</summary>
    public const string EnforceCheckDigit = "gate.enforce_check_digit";

    /// <summary>gecko_master 19_billing_settings.sql — a billable movement needs an unspent coupon (true = block, false = warn).</summary>
    public const string RequireCouponForCash = "gate.require_coupon_for_cash";
}
