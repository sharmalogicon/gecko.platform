using System.Globalization;
using System.Linq.Expressions;
using Gecko.MasterData.Contracts;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Application;

/// <summary>
/// The implementation other modules get through <see cref="IMasterDataReferences"/>.
/// Uses this module's request-scoped, RLS-bound context: the same tenant the
/// caller is acting for, and nothing else.
/// </summary>
internal sealed class MasterDataReferences(MasterDataDbContext db, TenantSettingReader settings) : IMasterDataReferences
{
    private static List<string> Normalise(IEnumerable<string> codes) =>
        codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();

    public async Task<IReadOnlyDictionary<string, BranchRef>> BranchesAsync(IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        var ids = branchIds.Distinct().ToList();
        return await db.BranchProfiles.AsNoTracking()
            .Where(b => ids.Contains(b.BranchId))
            .Select(b => new BranchRef(b.BranchId, b.BranchCode, b.Timezone))
            .ToDictionaryAsync(b => b.BranchId.ToString(), ct);
    }

    public async Task<IReadOnlyDictionary<string, PartyRef>> PartiesAsync(IEnumerable<string> partyCodes, CancellationToken ct)
    {
        var codes = Normalise(partyCodes);
        return await db.Parties.AsNoTracking()
            .Where(p => codes.Contains(p.PartyCode))
            .Select(p => new PartyRef(
                p.PartyId, p.PartyCode, p.NameEn, p.IsActive,
                db.CustomerExtensions.Any(x => x.PartyId == p.PartyId),
                db.ShippingLineExtensions.Any(x => x.PartyId == p.PartyId),
                db.ForwarderExtensions.Any(x => x.PartyId == p.PartyId),
                db.HaulierExtensions.Any(x => x.PartyId == p.PartyId),
                db.ShippingLineExtensions.Where(x => x.PartyId == p.PartyId).Select(x => x.OperatorCode).FirstOrDefault(),
                p.PrimaryPhone))
            .ToDictionaryAsync(p => p.PartyCode, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyList<CodeDescription>> ChargeCodesForModuleAsync(string moduleCode, CancellationToken ct)
    {
        var module = moduleCode.ToUpperInvariant();
        return await db.ChargeCodes.AsNoTracking()
            .Where(c => c.ModuleCode == module && c.IsActive)
            .OrderBy(c => c.ChargeCode1)
            .Select(c => new CodeDescription(c.ChargeCode1, c.DescriptionEn))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CodeDescription>> ActiveOrderTypeCodesAsync(CancellationToken ct) =>
        await db.OrderTypes.AsNoTracking()
            .Where(o => o.IsActive)
            .OrderBy(o => o.OrderTypeCode)
            .Select(o => new CodeDescription(o.OrderTypeCode, o.DescriptionEn))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CodeDescription>> MovementCodesForModuleAsync(string moduleCode, CancellationToken ct)
    {
        var module = moduleCode.ToUpperInvariant();
        return await db.Movements.AsNoTracking()
            .Where(m => m.IsActive && (m.AppliesToModule == module || m.AppliesToModule == "BOTH"))
            .OrderBy(m => m.MovementCode)
            .Select(m => new CodeDescription(m.MovementCode, m.DescriptionEn))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EquipmentTypeItem>> ActiveEquipmentTypesAsync(CancellationToken ct)
    {
        var rows = await db.EquipmentTypes.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.LengthFt).ThenBy(e => e.TypeCode)
            .Select(e => new { e.TypeCode, e.LengthFt, e.DescriptionEn })
            .ToListAsync(ct);
        return rows.Select(e => new EquipmentTypeItem(e.TypeCode,
            decimal.ToInt32(e.LengthFt).ToString(CultureInfo.InvariantCulture), e.DescriptionEn)).ToList();
    }

    public async Task<IReadOnlyList<CodeDescription>> CodeListAsync(string categoryCode, CancellationToken ct)
    {
        var category = categoryCode.ToUpperInvariant();
        var found = await db.VwCodeLists.AsNoTracking()
            .Where(v => v.CategoryCode == category && v.IsActive == true)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Code)
            .Select(v => new { Code = v.Code!, v.DescriptionEn })
            .ToListAsync(ct);
        // A code can appear twice (global and tenant-defined); keep the first in sort order.
        return found.DistinctBy(v => v.Code, StringComparer.OrdinalIgnoreCase)
            .Select(v => new CodeDescription(v.Code, v.DescriptionEn ?? "")).ToList();
    }

    public async Task<IReadOnlyDictionary<string, ChargeCodeRef>> ChargeCodesAsync(IEnumerable<string> chargeCodes, CancellationToken ct)
    {
        var codes = Normalise(chargeCodes);
        return await db.ChargeCodes.AsNoTracking()
            .Where(c => codes.Contains(c.ChargeCode1))
            .Select(c => new ChargeCodeRef(c.ChargeCodeId, c.ChargeCode1, c.ModuleCode, c.BillingUnitCode, c.IsActive))
            .ToDictionaryAsync(c => c.ChargeCode, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, CodeRef>> OrderTypesAsync(IEnumerable<string> orderTypeCodes, CancellationToken ct)
    {
        var codes = Normalise(orderTypeCodes);
        return await db.OrderTypes.AsNoTracking()
            .Where(o => codes.Contains(o.OrderTypeCode))
            .Select(o => new CodeRef(o.OrderTypeId, o.OrderTypeCode, o.IsActive))
            .ToDictionaryAsync(o => o.Code, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, CodeRef>> MovementsAsync(IEnumerable<string> movementCodes, CancellationToken ct)
    {
        var codes = Normalise(movementCodes);
        return await db.Movements.AsNoTracking()
            .Where(m => codes.Contains(m.MovementCode))
            .Select(m => new CodeRef(m.MovementId, m.MovementCode, m.IsActive))
            .ToDictionaryAsync(m => m.Code, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, EquipmentTypeRef>> EquipmentTypesAsync(IEnumerable<string> typeCodes, CancellationToken ct)
    {
        var codes = Normalise(typeCodes);
        var rows = await db.EquipmentTypes.AsNoTracking()
            .Where(e => codes.Contains(e.TypeCode))
            .Select(e => new { e.EquipmentTypeId, e.TypeCode, e.LengthFt, e.IsReefer, e.IsOog, e.IsActive, e.Teu, e.HeightClass })
            .ToListAsync(ct);

        // Length is DECIMAL(4,1) — 20.0 — and tariffs speak of "20".
        return rows.ToDictionary(
            e => e.TypeCode,
            e => new EquipmentTypeRef(e.EquipmentTypeId, e.TypeCode,
                decimal.ToInt32(e.LengthFt).ToString(CultureInfo.InvariantCulture), e.IsReefer, e.IsOog, e.IsActive, e.Teu, e.HeightClass),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> CodeListValuesAsync(string categoryCode, IEnumerable<string> codes, CancellationToken ct)
    {
        var category = categoryCode.ToUpperInvariant();
        var wanted = Normalise(codes);
        var found = await db.VwCodeLists.AsNoTracking()
            .Where(v => v.CategoryCode == category && v.IsActive == true && wanted.Contains(v.Code!))
            .Select(v => v.Code!)
            .ToListAsync(ct);
        return found.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool> GetBoolSettingAsync(string settingKey, Guid? branchId, bool fallback, CancellationToken ct) =>
        settings.GetBoolAsync(settingKey, branchId, fallback, ct);

    public Task<int> GetIntSettingAsync(string settingKey, Guid? branchId, int fallback, CancellationToken ct) =>
        settings.GetIntAsync(settingKey, branchId, fallback, ct);

    public Task<string?> GetStringSettingAsync(string settingKey, Guid? branchId, CancellationToken ct) =>
        settings.GetAsync(settingKey, branchId, ct);

    public async Task<IReadOnlyDictionary<string, VesselRef>> VesselsAsync(IEnumerable<string> vesselCodes, CancellationToken ct)
    {
        var codes = Normalise(vesselCodes);
        return await db.Vessels.AsNoTracking()
            .Where(v => codes.Contains(v.VesselCode))
            .Select(v => new VesselRef(v.VesselId, v.VesselCode, v.VesselName, v.ImoNumber, v.IsActive))
            .ToDictionaryAsync(v => v.VesselCode, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, PortRef>> PortsAsync(IEnumerable<string> portCodes, CancellationToken ct)
    {
        var codes = Normalise(portCodes);
        return await db.Ports.AsNoTracking()
            .Where(p => codes.Contains(p.PortCode))
            .Select(p => new PortRef(p.PortId, p.PortCode, p.PortNameEn, p.UnLocode, p.Timezone, p.IsActive, p.TradeMode))
            .ToDictionaryAsync(p => p.PortCode, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, CodeRef>> TerminalsAsync(IEnumerable<string> terminalCodes, CancellationToken ct)
    {
        var codes = Normalise(terminalCodes);
        return await db.Locations.AsNoTracking()
            .Where(l => l.LocationType == "TERMINAL" && codes.Contains(l.LocationCode))
            .Select(l => new CodeRef(l.LocationId, l.LocationCode, l.IsActive))
            .ToDictionaryAsync(l => l.Code, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, OrderTypePlanRef>> OrderTypePlansAsync(IEnumerable<string> orderTypeCodes, CancellationToken ct)
    {
        var codes = Normalise(orderTypeCodes);
        var orderTypes = await db.OrderTypes.AsNoTracking()
            .Where(o => codes.Contains(o.OrderTypeCode))
            .Select(o => new { o.OrderTypeId, o.OrderTypeCode, o.IsActive, o.DirectionCode, o.CargoClassCode, o.BookingTypeCode, o.RequiresVesselSchedule })
            .ToListAsync(ct);
        var ids = orderTypes.Select(o => o.OrderTypeId).ToList();

        var steps = (await (
                from otm in db.OrderTypeMovements.AsNoTracking()
                join m in db.Movements on otm.MovementId equals m.MovementId
                where ids.Contains(otm.OrderTypeId)
                orderby otm.SequenceNo
                select new
                {
                    otm.OrderTypeId,
                    Step = new OrderTypeStepRef(otm.OrderTypeMovementId, otm.MovementId, m.MovementCode, otm.SequenceNo,
                        otm.IsRequired, otm.IsBillable, otm.CheckSealNo, otm.CheckGrossWeight, otm.RequireVesselVoyage,
                        otm.AllowDamagedRelease, otm.SkipEdi,
                        m.Direction, m.FullEmpty, m.RequiresSurvey, m.ChangesYardPosition),
                }).ToListAsync(ct))
            .ToLookup(x => x.OrderTypeId, x => x.Step);

        return orderTypes.ToDictionary(
            o => o.OrderTypeCode,
            o => new OrderTypePlanRef(o.OrderTypeId, o.OrderTypeCode, o.IsActive, o.DirectionCode, o.CargoClassCode,
                o.BookingTypeCode, steps[o.OrderTypeId].ToList(), o.RequiresVesselSchedule),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, ContainerRef>> ContainersAsync(IEnumerable<string> containerNos, CancellationToken ct)
    {
        var numbers = Normalise(containerNos);
        var rows = await (
            from c in db.Containers.AsNoTracking()
            join t in db.EquipmentTypes on c.EquipmentTypeId equals t.EquipmentTypeId into types
            from t in types.DefaultIfEmpty()
            where numbers.Contains(c.ContainerNo)
            select new { c.ContainerId, c.ContainerNo, TypeCode = t == null ? null : t.TypeCode, c.Status, c.IsCheckDigitValid, c.FixedPortCodes, c.OwnerPartyId })
            .ToListAsync(ct);
        return rows.ToDictionary(c => c.ContainerNo,
            c => new ContainerRef(c.ContainerId, c.ContainerNo, c.TypeCode, c.Status, c.IsCheckDigitValid,
                (c.FixedPortCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), c.OwnerPartyId),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, CommodityRef>> CommoditiesAsync(IEnumerable<string> commodityCodes, CancellationToken ct)
    {
        var codes = Normalise(commodityCodes);
        return await db.Commodities.AsNoTracking()
            .Where(c => codes.Contains(c.CommodityCode))
            .Select(c => new CommodityRef(c.CommodityId, c.CommodityCode, c.IsDangerous, c.IsTemperatureControlled, c.IsActive))
            .ToDictionaryAsync(c => c.CommodityCode, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyDictionary<string, CodeRef>> ContainerGradesAsync(IEnumerable<string> gradeCodes, CancellationToken ct)
    {
        var codes = Normalise(gradeCodes);
        return await db.ContainerGrades.AsNoTracking()
            .Where(g => codes.Contains(g.GradeCode))
            .Select(g => new CodeRef(g.ContainerGradeId, g.GradeCode, g.IsActive))
            .ToDictionaryAsync(g => g.Code, StringComparer.OrdinalIgnoreCase, ct);
    }

    /// <summary>
    /// Resolves DELETED hold types too. TOS asks by the code on holds already
    /// placed; a hold type deleted in master data must not free the boxes that
    /// carry it (the barrier would read no scope, so no block) nor strand them
    /// (release needs the release authority). A deleted type comes back with
    /// IsActive = false, so nobody places a new hold of it. When a code was
    /// deleted and created again, the live row wins.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, HoldRef>> HoldsAsync(IEnumerable<string> holdCodes, CancellationToken ct)
    {
        var codes = Normalise(holdCodes);
        var rows = await db.Holds.IgnoreQueryFilters().AsNoTracking()   // soft-delete filter only; RLS still scopes the tenant
            .Where(h => codes.Contains(h.HoldCode))
            .OrderBy(h => h.DeletedAt != null).ThenByDescending(h => h.DeletedAt)
            .Select(h => new HoldRef(
                h.HoldId, h.HoldCode, h.DescriptionEn, h.HoldType,
                h.BlockingScope, h.ReleaseAuthority, h.Priority, h.DisplayColorHex, h.IsActive && h.DeletedAt == null, h.AutoApplyOnEvent))
            .ToListAsync(ct);
        return rows.GroupBy(h => h.HoldCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<HoldRef>> ActiveHoldsAsync(CancellationToken ct) =>
        await db.Holds.AsNoTracking().Where(h => h.IsActive)
            .OrderBy(h => h.Priority).ThenBy(h => h.HoldCode).Select(ToHoldRef).ToListAsync(ct);

    /// <summary>Filter FIRST, project second: a Where over the projection is not SQL.</summary>
    private static readonly Expression<Func<Hold, HoldRef>> ToHoldRef = h => new HoldRef(
        h.HoldId, h.HoldCode, h.DescriptionEn, h.HoldType,
        h.BlockingScope, h.ReleaseAuthority, h.Priority, h.DisplayColorHex, h.IsActive, h.AutoApplyOnEvent);

    public async Task<IReadOnlyList<OrderTypeChargeRef>> OrderTypeChargesAsync(string orderTypeCode, CancellationToken ct)
    {
        var code = orderTypeCode.Trim().ToUpperInvariant();
        return await (
                from otc in db.OrderTypeCharges.AsNoTracking()
                join ot in db.OrderTypes on otc.OrderTypeId equals ot.OrderTypeId
                join cc in db.ChargeCodes on otc.ChargeCodeId equals cc.ChargeCodeId
                join m in db.Movements on otc.MovementId equals m.MovementId into movement
                from m in movement.DefaultIfEmpty()
                where ot.OrderTypeCode == code && cc.IsActive
                orderby cc.ChargeCode1
                select new OrderTypeChargeRef(cc.ChargeCodeId, cc.ChargeCode1, m == null ? null : m.MovementCode,
                    otc.PaymentTo, otc.PaymentTermCode, otc.IsDefault, otc.IsOptional, otc.IsValueAddedService,
                    otc.RaiseAtGateIn, otc.DefaultQty))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HaulierChargeTermRef>> HaulierChargeTermsAsync(string haulierCode, string orderTypeCode, CancellationToken ct)
    {
        var haulier = haulierCode.Trim().ToUpperInvariant();
        var orderType = orderTypeCode.Trim().ToUpperInvariant();
        return await db.HaulierChargeTerms.AsNoTracking()
            .Where(t => t.HaulierPartyCode == haulier && t.OrderTypeCode == orderType)
            .OrderBy(t => t.MovementCode).ThenBy(t => t.ChargeCode)
            .Select(t => new HaulierChargeTermRef(t.HaulierPartyCode, t.OrderTypeCode, t.MovementCode, t.ChargeCode, t.PaymentTermCode))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChargeVariantRef>> ChargeVariantsAsync(IEnumerable<string> chargeCodes, CancellationToken ct)
    {
        var codes = Normalise(chargeCodes);
        return await Variants(db.ChargeCodes.Where(c => codes.Contains(c.ChargeCode1))).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChargeVariantRef>> ChargeVariantsOfTypeAsync(string chargeType, CancellationToken ct)
    {
        var type = chargeType.Trim().ToUpperInvariant();
        return await Variants(db.ChargeCodes.Where(c => c.ChargeType == type && c.IsActive)).ToListAsync(ct);
    }

    private IQueryable<ChargeVariantRef> Variants(IQueryable<ChargeCode> charges) =>
        from cc in charges.AsNoTracking()
        join v in db.ChargeCodeVariants on cc.ChargeCodeId equals v.ChargeCodeId
        join t in db.TaxCodes on v.TaxCodeId equals t.TaxCodeId into tax
        from t in tax.DefaultIfEmpty()
        where v.IsActive
        orderby cc.ChargeCode1, v.BillTo, v.PaymentTermCode
        select new ChargeVariantRef(cc.ChargeCodeId, cc.ChargeCode1, cc.DescriptionEn, cc.DescriptionLocal,
            cc.ChargeType, cc.ChargeCategory, cc.BillingUnitCode, v.BillTo, v.PaymentTermCode,
            t == null ? null : t.TaxCode1, t == null ? 0m : t.RatePct, v.CreditTermDays);

    public async Task<int?> YardCapacityTeuAsync(Guid branchId, CancellationToken ct)
    {
        var stated = await db.Yards.AsNoTracking()
            .Where(y => y.BranchId == branchId && y.IsActive && y.CapacityTeu != null)
            .Select(y => y.CapacityTeu!.Value)
            .ToListAsync(ct);
        return stated.Count == 0 ? null : stated.Sum();
    }

    public async Task<InvoicingCompanyRef?> InvoicingCompanyAsync(Guid branchId, CancellationToken ct)
    {
        var row = await (
                from bp in db.BranchProfiles.AsNoTracking()
                join co in db.Companies on bp.CompanyId equals co.CompanyId
                where bp.BranchId == branchId
                select new { Branch = bp, Company = co })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var (b, c) = (row.Branch, row.Company);

        // The issuing branch's own address when it records one, else the company's — never a mix of the two.
        var address = !string.IsNullOrWhiteSpace(b.Address1)
            ? JoinAddress(b.Address1, b.Address2, b.City, b.State, b.Postcode)
            : JoinAddress(c.Address1, c.Address2, c.City, c.State, c.Postcode);
        var taxBranch = Blank(b.TaxBranchCode) ?? Blank(c.TaxBranchCode);

        return new InvoicingCompanyRef(c.CompanyId, c.CompanyCode, c.NameEn, Blank(c.NameLocal),
            Blank(c.TaxId), taxBranch, taxBranch is null ? null : taxBranch.Trim('0').Length == 0,
            address, Blank(b.Phone) ?? Blank(c.Phone), Blank(b.Email) ?? Blank(c.Email));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? JoinAddress(string? line1, string? line2, string? city, string? state, string? postcode)
    {
        var tail = string.Join(" ", new[] { state, postcode }.Select(Blank).OfType<string>());
        var parts = new[] { line1, line2, city, tail }.Select(Blank).OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public async Task<SurveyCodeSets> SurveyCodesAsync(CancellationToken ct)
    {
        var damages = await db.DamageCodes.AsNoTracking().Where(d => d.IsActive)
            .Select(d => new DamageCodeRef(d.DamageCode1, d.DescriptionEn, d.Severity, d.MakesUnserviceable))
            .ToDictionaryAsync(d => d.DamageCode, StringComparer.OrdinalIgnoreCase, ct);

        var components = await db.Components.AsNoTracking().Where(c => c.IsActive).Select(c => c.ComponentCode).ToListAsync(ct);
        var locations = await db.DamageLocations.AsNoTracking().Where(l => l.IsActive).Select(l => l.LocationCode).ToListAsync(ct);

        return new SurveyCodeSets(
            damages,
            components.ToHashSet(StringComparer.OrdinalIgnoreCase),
            locations.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<GateHoursStatus?> GateHoursStatusAsync(Guid branchId, DateTimeOffset at, CancellationToken ct)
    {
        var windows = await db.GateHoursWindows.AsNoTracking().Where(w => w.BranchId == branchId)
            .Select(w => new GateHoursCalendar.Window(w.IsoWeekday, w.OpensAt, w.ClosesAt)).ToListAsync(ct);
        if (windows.Count == 0) return null;

        var zoneId = await db.BranchProfiles.AsNoTracking().Where(b => b.BranchId == branchId).Select(b => b.Timezone).FirstOrDefaultAsync(ct);
        var zone = GateHoursCalendar.Zone(zoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var from = today.AddDays(-1);
        var to = today.AddDays(GateHoursCalendar.LookAheadDays);

        var exceptions = (await db.GateHoursExceptions.AsNoTracking()
                .Where(e => e.BranchId == branchId && e.ExceptionDate >= from && e.ExceptionDate <= to).ToListAsync(ct))
            .ToDictionary(e => e.ExceptionDate, e => new GateHoursCalendar.DateException(e.ExceptionDate, e.IsClosed, e.OpensAt, e.ClosesAt, e.Reason));

        // A depot's own holiday outranks the tenant-wide one on the same date.
        var holidays = (await db.PublicHolidays.AsNoTracking()
                .Where(h => h.HolidayDate >= from && h.HolidayDate <= to && (h.BranchId == null || h.BranchId == branchId)).ToListAsync(ct))
            .OrderBy(h => h.BranchId is null ? 0 : 1)
            .GroupBy(h => h.HolidayDate)
            .ToDictionary(g => g.Key, g => g.Select(h => new GateHoursCalendar.HolidayDay(h.HolidayDate, h.NameEn, h.IsHalfDay)).Last());

        return GateHoursCalendar.Resolve(at, zone, windows, exceptions, holidays);
    }
}
