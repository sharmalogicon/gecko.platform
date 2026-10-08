using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application.Reports;

/// <summary>The Gate In/Out report's parameters: Vector's, with its BranchID / YardLocation as Gecko's branch and yard.</summary>
internal sealed record GateInOutFilter(
    DateOnly From, DateOnly To,
    string? AgentCode = null, string? ForwarderCode = null, string? CustomerCode = null,
    string? VesselCode = null, string? VoyageNo = null, string? Size = null, string? Type = null, string? FullEmpty = null,
    string? BookingType = null, string? OrderType = null, string? BookingBlNo = null, string? HaulierCode = null,
    string? MovementCode = null, Guid? YardId = null);

/// <summary>
/// Vector's TMS.Operation.InBound.GateInOut (proc Report.usp_Inbound_GateInOut) over Gecko's gate:
/// every standing move at the depot in the depot days asked for, ONE ROW PER BOOKING + BOX (the RDL
/// groups its detail rows by OrderNo, ContainerNo: the first move's values, its item number, and the
/// group's VGM summed), the box's Empty In / Empty Out / Full In / Full Out on that booking — or, when
/// the report is asked for MTY IN moves, the box's next empty-out / full-in / full-out at the depot on
/// or after the day it came in — and the per-agent size × type count of every move.
/// </summary>
internal static class GateInOutReport
{
    public const string EmptyInCode = "MTY_IN";

    public static readonly IReadOnlyList<ReportColumn> Columns =
    [
        new("item", 1.1377), new("Booking BLNo", 2.5), new("Container No", 2.42125), new("Size", 1.15062), new("Status", 1.58687),
        new("Empty In", 2.71042, "dd-MM-yyyy HH:mm"), new("Empty Out", 2.5, "dd-MM-yyyy HH:mm"),
        new("Full In", 2.5, "dd-MM-yyyy HH:mm"), new("Full Out", 2.5, "dd-MM-yyyy HH:mm"),
        new("Consignee / Shipper ", 4.59021), new("Booking Type", 2.42063), new("Current Movement", 3.09438), new("Agent", 2.19666),
        new("Vessel Name", 3.87583), new("Voyage No", 1.99729), new("Plate No", 1.78563), new("Truck Company", 2.87042),
        new("Seal Agent", 2.55445), new("Seal Customs", 2.5), new("VGM", 2.07667, "0.00"), new("Booking Temp", 1.73271),
        new("Temp In", 1.30938), new("Genset ", 1.4152), new("Prev Location", 2.10313), new("Next Location", 1.97083),
    ];

    private sealed record Move(
        int RowId, Guid GateTransactionId, Guid BookingContainerId, Guid BookingId, string OrderNo, string ContainerNo,
        string? BookingBlNo, string? EquipmentTypeCode, string? ConditionCode, DateTime At, string MovementCode,
        string Direction, string FullEmpty, string? CustomerCode, string BookingTypeCode, string LineCode,
        string? VesselCode, string? VoyageNo, string TruckPlate, string? HaulierCode, decimal? Vgm,
        decimal? BookingTemp, decimal? TempIn, string? GensetNo, string? NextPrevLocation);

    public static async Task<ListReport> BuildAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, string branchName, string printedBy,
        DateTimeOffset printedAt, GateInOutFilter f, CancellationToken ct)
    {
        var start = BranchClock.StartOf(branch, f.From);
        var end = BranchClock.StartOf(branch, f.To.AddDays(1));

        var rows =
            from g in db.GateTransactions.AsNoTracking()
            where g.BranchId == branch.BranchId && g.Status == "COMPLETED" && g.TransactionAt >= start && g.TransactionAt < end
            join b in db.Bookings on g.BookingId equals b.BookingId
            join v in db.TruckVisits on g.TruckVisitId equals v.TruckVisitId
            join bc in db.BookingContainers on g.BookingContainerId equals bc.BookingContainerId
            join er in db.EquipmentRequirements on bc.EquipmentRequirementId equals er.EquipmentRequirementId
            join vc in db.VesselCalls on b.VesselCallId equals vc.VesselCallId into calls
            from vc in calls.DefaultIfEmpty()
            select new { g, b, v.TruckPlate, v.HaulierPartyCode, BoxTemp = bc.ReeferSetTempC, LineTemp = er.ReeferSetTempC, Call = vc };

        if (Clean(f.AgentCode) is { } agent) rows = rows.Where(r => r.g.LinePartyCode == agent);
        if (Clean(f.ForwarderCode) is { } forwarder) rows = rows.Where(r => r.b.ForwarderPartyCode == forwarder);
        if (Clean(f.CustomerCode) is { } customer) rows = rows.Where(r => r.b.CustomerPartyCode == customer);
        if (Clean(f.VesselCode) is { } vessel) rows = rows.Where(r => r.Call != null && r.Call.VesselCode == vessel);
        if (Clean(f.VoyageNo) is { } voyage) rows = rows.Where(r => r.Call != null && (r.Call.OperatorVoyageIn == voyage || r.Call.OperatorVoyageOut == voyage));
        if (await TypeCodesAsync(master, f.Size, f.Type, ct) is { } codes) rows = rows.Where(r => r.g.EquipmentTypeCode != null && codes.Contains(r.g.EquipmentTypeCode));
        if (Clean(f.FullEmpty) is { } load) rows = rows.Where(r => r.g.FullEmpty == load);
        if (Clean(f.BookingType) is { } bookingType) rows = rows.Where(r => r.b.BookingTypeCode == bookingType);
        if (Clean(f.OrderType) is { } orderType) rows = rows.Where(r => r.b.OrderTypeCode == orderType);
        if (f.BookingBlNo?.Trim() is { Length: > 0 } blNo) rows = rows.Where(r => r.b.CarrierRef == blNo || r.b.CustomerRef == blNo || r.b.SubBlNo == blNo);
        if (Clean(f.HaulierCode) is { } haulier) rows = rows.Where(r => r.HaulierPartyCode == haulier);
        if (Clean(f.MovementCode) is { } movement) rows = rows.Where(r => r.g.MovementCode == movement);
        if (f.YardId is { } yard) rows = rows.Where(r => r.g.YardId == yard);

        var found = await rows.OrderBy(r => r.g.TransactionAt).ThenBy(r => r.g.EirNo).ToListAsync(ct);
        var moves = found.Select((r, i) => new Move(
            i + 1, r.g.GateTransactionId, r.g.BookingContainerId, r.b.BookingId, r.b.OrderNo, r.g.ContainerNo,
            BookingBlNo(r.b.CarrierRef, r.b.CustomerRef), r.g.EquipmentTypeCode, r.g.ConditionCode,
            Local(branch, r.g.TransactionAt), r.g.MovementCode, r.g.Direction, r.g.FullEmpty, r.b.CustomerPartyCode,
            r.b.BookingTypeCode, r.g.LinePartyCode, r.Call?.VesselCode, Voyage(r.b.DirectionCode, r.Call?.OperatorVoyageIn, r.Call?.OperatorVoyageOut),
            r.TruckPlate, r.HaulierPartyCode, r.g.VgmKg, r.BoxTemp ?? r.LineTemp, r.g.TempObservedC, r.g.GensetNo, r.b.NextPrevLocation)).ToList();

        // Seals: the agent's (line / agent) and the customs' seal of each move — Vector's SealNo1 / SealNo2.
        var ids = moves.Select(m => m.GateTransactionId).ToList();
        var seals = (await db.GateTransactionSeals.AsNoTracking().Where(s => ids.Contains(s.GateTransactionId) && s.DeletedAt == null)
                .OrderBy(s => s.CreatedAt).Select(s => new { s.GateTransactionId, s.SealNo, s.SealType }).ToListAsync(ct))
            .ToLookup(s => s.GateTransactionId);
        string? Seal(Guid id, Func<string, bool> kind) => seals[id].FirstOrDefault(s => kind(s.SealType))?.SealNo;

        // The four dates: the box's moves on that booking line (Vector BookingContainer.EmptyInDate …),
        // or for an MTY IN report its next moves at the depot from the day it came in.
        var emptyInReport = string.Equals(Clean(f.MovementCode), EmptyInCode, StringComparison.Ordinal);
        var lines = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var boxes = moves.Select(m => m.ContainerNo).Distinct().ToList();
        var later = await db.GateTransactions.AsNoTracking()
            .Where(g => g.Status == "COMPLETED" && g.BranchId == branch.BranchId
                        && (emptyInReport ? boxes.Contains(g.ContainerNo) && g.TransactionAt >= start : lines.Contains(g.BookingContainerId)))
            .Select(g => new { g.BookingContainerId, g.ContainerNo, g.Direction, g.FullEmpty, g.TransactionAt })
            .ToListAsync(ct);
        DateTime? First(Move m, string direction, string load)
        {
            var set = emptyInReport
                ? later.Where(x => x.ContainerNo == m.ContainerNo && Local(branch, x.TransactionAt).Date >= m.At.Date)
                : later.Where(x => x.BookingContainerId == m.BookingContainerId);
            return set.Where(x => x.Direction == direction && x.FullEmpty == load)
                .OrderBy(x => x.TransactionAt).Select(x => (DateTime?)Local(branch, x.TransactionAt)).FirstOrDefault();
        }

        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).Concat(moves.Select(m => m.HaulierCode)).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(moves.Select(m => m.VesselCode).OfType<string>().Distinct(), ct);
        var movementNames = (await master.MovementCodesForModuleAsync("TOS", ct))
            .GroupBy(m => m.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);

        var grid = moves.GroupBy(m => (m.OrderNo, m.ContainerNo)).Select(g =>
        {
            var m = g.First();
            var (size, type) = SizeType(types, m.EquipmentTypeCode);
            var vgm = g.Where(x => x.Vgm is not null).Select(x => x.Vgm!.Value).ToList();
            return new object?[]
            {
                m.RowId, m.BookingBlNo, m.ContainerNo, $"{size}'{type}", m.ConditionCode,
                emptyInReport ? m.At : First(m, GateRules.In, GateRules.Empty),
                First(m, GateRules.Out, GateRules.Empty), First(m, GateRules.In, GateRules.Full), First(m, GateRules.Out, GateRules.Full),
                m.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null,
                m.BookingTypeCode, movementNames.GetValueOrDefault(m.MovementCode) ?? m.MovementCode, m.LineCode,
                m.VesselCode is { } vc ? vessels.GetValueOrDefault(vc)?.VesselName : null, m.VoyageNo,
                m.TruckPlate, m.HaulierCode is { } h ? parties.GetValueOrDefault(h)?.Name : null,
                Seal(m.GateTransactionId, IsAgentSeal), Seal(m.GateTransactionId, t => t == "CUSTOMS"),
                vgm.Count == 0 ? null : vgm.Sum(), m.BookingTemp, m.TempIn, m.GensetNo, null, m.NextPrevLocation,
            };
        }).ToList();

        var summary = SummaryMatrix.Of(
            moves.Select(m => { var (size, type) = SizeType(types, m.EquipmentTypeCode); return ((string?)m.LineCode, (string?)size, (string?)type); }),
            caption: "SUMMARY", splitHeader: false, totalColumnLabel: "SUM", totalRowLabel: "Total");

        return new ListReport(
            FileName: $"GateInOut_{f.From:yyyyMMdd}-{f.To:yyyyMMdd}",
            MarginInches: 0.1,
            Heading: [new(branchName, 10, Bold: true), new(BookingTypeDescription(f.BookingType), 8, Bold: true)],
            HeadingRight: [new($"Printed By: {printedBy}", 8), new($"Printed On: {ListReport.Text(printedAt.DateTime, null)}", 8)],
            Preamble: [],
            Columns: Columns, Rows: grid, Summary: summary, PageLabel: "Page No#");
    }

    // ── shared by the list reports ──────────────────────────────────────────

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>Vector's one "Booking/B/L no": the carrier's booking number, else the customer's reference (KORAKIT's).</summary>
    public static string? BookingBlNo(string? carrierRef, string? customerRef) => carrierRef ?? customerRef;

    public static string? Voyage(string direction, string? voyageIn, string? voyageOut) =>
        direction == "EXPORT" ? voyageOut ?? voyageIn : voyageIn ?? voyageOut;

    /// <summary>The booking type as the heading prints it (Vector's BookingTypeDescription parameter): the code asked for, else blank.</summary>
    public static string BookingTypeDescription(string? bookingType) => Clean(bookingType) ?? "";

    /// <summary>A UTC instant as the depot's wall clock — Vector stored and printed local times.</summary>
    public static DateTime Local(BranchClock.Branch branch, DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, branch.Zone).DateTime;

    public static async Task<IReadOnlyDictionary<string, EquipmentTypeRef>> TypesAsync(
        IMasterDataReferences master, IEnumerable<string?> codes, CancellationToken ct)
    {
        var list = codes.OfType<string>().Distinct().ToList();
        return list.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(list, ct);
    }

    /// <summary>Vector's Size and Type: Gecko's type code is the two put together (20GP = 20 + GP).</summary>
    public static (string? Size, string? Type) SizeType(IReadOnlyDictionary<string, EquipmentTypeRef> types, string? code)
    {
        if (code is null) return (null, null);
        var size = types.GetValueOrDefault(code)?.SizeCode ?? (code.Length > 2 && char.IsDigit(code[0]) && char.IsDigit(code[1]) ? code[..2] : null);
        return size is not null && code.StartsWith(size, StringComparison.Ordinal) ? (size, code[size.Length..]) : (size, code);
    }

    /// <summary>The type codes a Size / Type filter means, or null when neither is asked for.</summary>
    public static async Task<List<string>?> TypeCodesAsync(IMasterDataReferences master, string? size, string? type, CancellationToken ct)
    {
        var (s, t) = (Clean(size), Clean(type));
        if (s is null && t is null) return null;
        var all = (await master.ActiveEquipmentTypesAsync(ct)).Select(e => e.TypeCode).ToList();
        var types = await TypesAsync(master, all, ct);
        return all.Where(c =>
        {
            var (cs, ctp) = SizeType(types, c);
            return (s is null || cs == s) && (t is null || ctp == t);
        }).ToList();
    }

    public static bool IsAgentSeal(string sealType) => sealType is "LINE" or "AGENT";
}
