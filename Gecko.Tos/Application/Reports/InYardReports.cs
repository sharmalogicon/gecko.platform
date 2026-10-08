using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;

namespace Gecko.Tos.Application.Reports;

/// <summary>
/// The in-yard reports' parameters: Vector's, with BranchID / YardLocation as Gecko's branch and yard.
/// The dates are printed in the heading and select nothing — Vector's procs took them and ignored them, and so does
/// Gecko (owner 2026-10-09): the report is the whole stock standing now.
/// </summary>
internal sealed record InYardFilter(
    DateOnly? From = null, DateOnly? To = null,
    string? AgentCode = null, string? OwnerCode = null, string? ForwarderCode = null, string? CustomerCode = null,
    string? VesselCode = null, string? VoyageNo = null, string? Size = null, string? Type = null, string? GradeCode = null,
    string? BookingType = null, string? OrderType = null, string? BookingBlNo = null, string? HaulierCode = null, Guid? YardId = null);

/// <summary>
/// Vector's TMS.Operation.OutBound.EmptyInYard (Report.usp_OutBound_EmptyInYard) and
/// TMS.Operation.OutBound.FullInYard (Report.usp_FullInYard) over Gecko's stock: every box standing
/// in the depot now, empty or full, with the move that brought it in, the booking it is on, and the
/// per-agent (per-owner, for empties) size Ã— type count.
/// </summary>
internal static class InYardReports
{
    public static readonly IReadOnlyList<ReportColumn> EmptyColumns =
    [
        new("Container No", 0.98611 * 2.54), new("Agent Code", 0.81944 * 2.54), new("Empty In", 0.73263 * 2.54, "dd-MM-yyyy"),
        new("Time", 0.43402 * 2.54, "H:mm"), new("Size", 0.3653 * 2.54), new("Type", 0.35345 * 2.54), new("Condition", 0.73958 * 2.54),
        new("Plate No.", 0.64583 * 2.54), new("Fix Port", 0.625 * 2.54), new("Gross  Weight", 0.63542 * 2.54, "#,0;(#,0)"),
        new("Grade", 0.46875 * 2.54), new("Booking No.", 1.33333 * 2.54), new("Vessel ", 0.59375 * 2.54), new("Voyage ", 0.59375 * 2.54),
        new("Shipper", 1.0 * 2.54), new("Prev Location", 0.8125 * 2.54), new("PTI 1", 0.69908 * 2.54, "dd-MM-yyyy"),
        new("PTI 2", 0.72942 * 2.54, "dd-MM-yyyy"), new("PTI 3", 0.6875 * 2.54, "dd-MM-yyyy"), new("Yard  Loacaton", 0.72917 * 2.54),
        new("Day", 0.46875 * 2.54), new("Remarks", 1.0 * 2.54),
    ];

    public static readonly IReadOnlyList<ReportColumn> FullColumns =
    [
        new("Container No", 1.0 * 2.54), new("Agent Code", 0.79625 * 2.54), new("Booking BLNo", 1.0 * 2.54),
        new("Full Date In", 1.03708 * 2.54, "dd-MM-yyyy HH:mm"), new("Size", 0.35417 * 2.54), new("Type", 0.39583 * 2.54),
        new("Order Type", 1.0 * 2.54), new("Container Status", 0.71875 * 2.54), new(" Day In Yard", 0.61458 * 2.54),
        new("Shipper", 1.0 * 2.54), new("Truck Comp", 1.0 * 2.54), new("Seal Agent", 1.0 * 2.54), new("Vessel Name", 1.0 * 2.54),
        new("Voyage No", 0.84178 * 2.54), new("Temp", 0.57292 * 2.54), new("Closing Time Port", 1.0 * 2.54, "dd/MM/yyyy HH:mm"),
        new("Remarks", 1.0 * 2.54),
    ];

    private sealed record Box(
        Guid GateInId, string ContainerNo, string LineCode, string? EquipmentTypeCode, string? ConditionCode, string? GradeCode,
        Guid? YardId, DateTimeOffset InAt, string TruckPlate, string? HaulierCode, decimal? MaxGrossKg, string? Remarks,
        Guid? BookingId, Guid? LinePartyId, string? OrderNo, string? BookingBlNo, string? OrderTypeCode, string? BookingTypeCode,
        string? DirectionCode, string? CustomerCode, string? ForwarderCode, string? NextPrevLocation,
        Guid? VesselCallId, string? VesselCode, string? VoyageIn, string? VoyageOut, decimal? ReeferTemp, string? ImdgClass);

    private static async Task<List<Box>> StockAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, string load, InYardFilter f, CancellationToken ct)
    {
        // The open stays: the box, the move that brought it, and the booking it is on now (else the one it came on).
        var rows =
            from v in db.ContainerVisits.AsNoTracking()
            where v.BranchId == branch.BranchId && v.GateOutTransactionId == null && v.DeletedAt == null && v.FullEmpty == load
            join gi in db.GateTransactions on v.GateInTransactionId equals gi.GateTransactionId
            join tv in db.TruckVisits on gi.TruckVisitId equals tv.TruckVisitId
            join cur in db.BookingContainers on v.CurrentBookingContainerId equals cur.BookingContainerId into curs
            from cur in curs.DefaultIfEmpty()
            join bc in db.BookingContainers on (cur != null ? cur.BookingContainerId : gi.BookingContainerId) equals bc.BookingContainerId
            join er in db.EquipmentRequirements on bc.EquipmentRequirementId equals er.EquipmentRequirementId
            join b in db.Bookings on bc.BookingId equals b.BookingId
            join vc in db.VesselCalls on b.VesselCallId equals vc.VesselCallId into calls
            from vc in calls.DefaultIfEmpty()
            select new { v, gi, tv, bc, er, b, vc };

        if (Clean(f.AgentCode) is { } agent) rows = rows.Where(r => r.v.LinePartyCode == agent);
        if (Clean(f.ForwarderCode) is { } forwarder) rows = rows.Where(r => r.b.ForwarderPartyCode == forwarder);
        if (Clean(f.CustomerCode) is { } customer) rows = rows.Where(r => r.b.CustomerPartyCode == customer);
        if (Clean(f.VesselCode) is { } vessel) rows = rows.Where(r => r.vc != null && r.vc.VesselCode == vessel);
        if (Clean(f.VoyageNo) is { } voyage) rows = rows.Where(r => r.vc != null && (r.vc.OperatorVoyageIn == voyage || r.vc.OperatorVoyageOut == voyage));
        if (await TypeCodesAsync(master, f.Size, f.Type, ct) is { } codes) rows = rows.Where(r => r.v.EquipmentTypeCode != null && codes.Contains(r.v.EquipmentTypeCode));
        if (Clean(f.GradeCode) is { } grade) rows = rows.Where(r => r.v.GradeCode == grade);
        if (Clean(f.BookingType) is { } bookingType) rows = rows.Where(r => r.b.BookingTypeCode == bookingType);
        if (Clean(f.OrderType) is { } orderType) rows = rows.Where(r => r.b.OrderTypeCode == orderType);
        if (f.BookingBlNo?.Trim() is { Length: > 0 } blNo) rows = rows.Where(r => r.b.CarrierRef == blNo || r.b.CustomerRef == blNo || r.b.SubBlNo == blNo);
        if (Clean(f.HaulierCode) is { } haulier) rows = rows.Where(r => r.tv.HaulierPartyCode == haulier);
        if (f.YardId is { } yard) rows = rows.Where(r => r.v.YardId == yard);

        return await rows.Select(r => new Box(
            r.gi.GateTransactionId, r.v.ContainerNo, r.v.LinePartyCode, r.v.EquipmentTypeCode, r.v.ConditionCode, r.v.GradeCode,
            r.v.YardId, r.gi.TransactionAt, r.tv.TruckPlate, r.tv.HaulierPartyCode, r.gi.MaxGrossWeightKg, r.gi.Remarks,
            r.b.BookingId, r.b.LinePartyId, r.b.OrderNo, r.b.CarrierRef ?? r.b.CustomerRef, r.b.OrderTypeCode, r.b.BookingTypeCode,
            r.b.DirectionCode, r.b.CustomerPartyCode, r.b.ForwarderPartyCode, r.b.NextPrevLocation,
            r.b.VesselCallId, r.vc == null ? null : r.vc.VesselCode, r.vc == null ? null : r.vc.OperatorVoyageIn, r.vc == null ? null : r.vc.OperatorVoyageOut,
            r.bc.ReeferSetTempC ?? r.er.ReeferSetTempC, r.bc.ImdgClass ?? r.er.ImdgClass)).ToListAsync(ct);
    }

    /// <summary>Whole days in the yard as Vector counted them: DATEDIFF(day, in, today) â€” the full report adds one.</summary>
    private static int Days(BranchClock.Branch branch, DateTimeOffset inAt, DateOnly today) =>
        today.DayNumber - DateOnly.FromDateTime(Local(branch, inAt)).DayNumber;

    public static async Task<ListReport> EmptyAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, string branchName, string printedBy,
        DateTimeOffset printedAt, DateOnly today, InYardFilter f, CancellationToken ct)
    {
        var boxes = await StockAsync(db, master, branch, GateRules.Empty, f, ct);

        var registry = await master.ContainersAsync(boxes.Select(b => b.ContainerNo).Distinct(), ct);
        string? Owner(Box b) => registry.GetValueOrDefault(b.ContainerNo)?.OwnerPartyCode;
        if (Clean(f.OwnerCode) is { } owner) boxes = boxes.Where(b => Owner(b) == owner).ToList();

        var types = await TypesAsync(master, boxes.Select(b => b.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(boxes.Select(b => b.CustomerCode).OfType<string>().Distinct(), ct);
        var yards = await master.YardsAsync(boxes.Select(b => b.YardId).OfType<Guid>().Distinct(), ct);

        // The RDL sorts by agent, then the empty-in date.
        boxes = boxes.OrderBy(b => b.LineCode, StringComparer.Ordinal).ThenBy(b => b.InAt).ToList();
        var grid = boxes.Select(b =>
        {
            var (size, type) = SizeType(types, b.EquipmentTypeCode);
            var reg = registry.GetValueOrDefault(b.ContainerNo);
            var inAt = Local(branch, b.InAt);
            return new object?[]
            {
                b.ContainerNo, Owner(b), inAt, inAt, size, type, b.ConditionCode, b.TruckPlate,
                reg?.FixedPortCodes is { Count: > 0 } ports ? string.Join(",", ports) : null,
                b.MaxGrossKg ?? reg?.MaxGrossKg, b.GradeCode, b.BookingBlNo,
                b.VesselCode, Voyage(b.DirectionCode ?? "", b.VoyageIn, b.VoyageOut),
                b.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, b.NextPrevLocation,
                null, null, null,   // PTI 1â€“3: Vector's M&R work orders; Gecko keeps none yet
                b.YardId is { } y ? yards.GetValueOrDefault(y)?.YardCode : null,
                Days(branch, b.InAt, today), b.Remarks,
            };
        }).ToList();

        var summary = SummaryMatrix.Of(
            boxes.Select(b => { var (size, type) = SizeType(types, b.EquipmentTypeCode); return (Owner(b), size, type); }),
            caption: null, splitHeader: true, totalColumnLabel: "Total", totalRowLabel: "GRAND TOTAL");

        return new ListReport(
            FileName: $"EmptyInYard_{today:yyyyMMdd}",
            MarginInches: 1,
            Heading: [new(branchName, 10, Bold: true), new("<< OUTBOUND EMPTY CONTAINER IN YARD >>", 10, Bold: true)],
            HeadingRight: [new($"Printed By:  {printedBy}", 8), new($"Print Date:  {ListReport.Text(printedAt.DateTime, null)}", 8)],
            Preamble:
            [
                new($"From Empty In : {Day(f.From)} To Empty In : {Day(f.To)}", 10),
                new($"Agent :  {Clean(f.AgentCode)}", 8),
            ],
            Columns: EmptyColumns, Rows: grid, Summary: summary, PageLabel: "Page No #");
    }

    public static async Task<ListReport> FullAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, string branchName, string printedBy,
        DateTimeOffset printedAt, DateOnly today, InYardFilter f, CancellationToken ct)
    {
        var boxes = await StockAsync(db, master, branch, GateRules.Full, f, ct);

        var types = await TypesAsync(master, boxes.Select(b => b.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(
            boxes.Select(b => b.CustomerCode).Concat(boxes.Select(b => b.HaulierCode)).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(boxes.Select(b => b.VesselCode).OfType<string>().Distinct(), ct);

        var ids = boxes.Select(b => b.GateInId).ToList();
        var seals = (await db.GateTransactionSeals.AsNoTracking().Where(s => ids.Contains(s.GateTransactionId) && s.DeletedAt == null)
                .OrderBy(s => s.CreatedAt).Select(s => new { s.GateTransactionId, s.SealNo, s.SealType }).ToListAsync(ct))
            .ToLookup(s => s.GateTransactionId);

        // Closing Time Port: the call's port cut-off that applies to this box (reefer / DG / dry), line and depot first.
        var cutoffs = new Dictionary<(Guid, Guid?), IReadOnlyList<EffectiveCutoff>>();
        foreach (var key in boxes.Where(b => b.VesselCallId is not null).Select(b => (b.VesselCallId!.Value, b.LinePartyId)).Distinct())
            cutoffs[key] = await CutoffLookup.EffectiveAsync(db, key.Item1, key.Item2, branch.BranchId, ct);
        DateTime? PortCutoff(Box b, bool reefer)
        {
            if (b.VesselCallId is not { } call || !cutoffs.TryGetValue((call, b.LinePartyId), out var list)) return null;
            var kind = b.ImdgClass is not null ? "PORT_DG" : reefer ? "PORT_REEFER" : "PORT_DRY";
            var hit = list.FirstOrDefault(c => c.Kind == kind) ?? list.FirstOrDefault(c => c.Kind == "PORT_DRY");
            return hit is null ? null : Local(branch, hit.At);
        }

        int DaysIn(Box b) => Days(branch, b.InAt, today) + 1;
        // The RDL sorts by agent, then the days in the yard.
        boxes = boxes.OrderBy(b => b.LineCode, StringComparer.Ordinal).ThenBy(DaysIn).ToList();
        var grid = boxes.Select(b =>
        {
            var (size, type) = SizeType(types, b.EquipmentTypeCode);
            var reefer = b.EquipmentTypeCode is { } c && types.GetValueOrDefault(c)?.IsReefer == true;
            return new object?[]
            {
                b.ContainerNo, b.LineCode, b.BookingBlNo, Local(branch, b.InAt), size, type, b.OrderTypeCode, b.ConditionCode, DaysIn(b),
                b.CustomerCode is { } cu ? parties.GetValueOrDefault(cu)?.Name : null,
                b.HaulierCode is { } h ? parties.GetValueOrDefault(h)?.Name : null,
                seals[b.GateInId].FirstOrDefault(s => IsAgentSeal(s.SealType))?.SealNo,
                b.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName : null,
                Voyage(b.DirectionCode ?? "", b.VoyageIn, b.VoyageOut), b.ReeferTemp, PortCutoff(b, reefer), b.Remarks,
            };
        }).ToList();

        var summary = SummaryMatrix.Of(
            boxes.Select(b => { var (size, type) = SizeType(types, b.EquipmentTypeCode); return ((string?)b.LineCode, size, type); }),
            caption: null, splitHeader: true, totalColumnLabel: "Total", totalRowLabel: "GRAND TOTAL");

        return new ListReport(
            FileName: $"FullInYard_{today:yyyyMMdd}",
            MarginInches: 1,
            Heading:
            [
                new(branchName, 10, Bold: true),
                new($"<< {BookingTypeDescription(f.BookingType)} FULL CONTAINER IN YARD >>", 10, Bold: true),
            ],
            HeadingRight: [new($"Printed By.  {printedBy}", 8), new($"Printed On.  {printedAt.DateTime:dd/MM/yyyy HH:mm}", 8)],
            Preamble:
            [
                new($"From Full In : {Day(f.From)} To Full In : {Day(f.To)}", 10),
                new($"Agent :  {Clean(f.AgentCode)}", 10),
            ],
            Columns: FullColumns, Rows: grid, Summary: summary, PageLabel: "Page No #");
    }

    private static string Day(DateOnly? day) => day?.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
