using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;

namespace Gecko.Tos.Application.Reports.Forms;

/// <summary>A booked box as the forms list it: an un-nominated place has no number (it still prints a numbered line).</summary>
internal sealed record BookingFormBox(string ContainerNo, string? Size, string? Type);

/// <summary>
/// What Vector's Report.usp_Booking_CustomForm returned for one booking, the fields every booking form prints, from
/// Gecko's own booking (owner 2026-10-11 defaults): AgentName the booking's agent, else its line; CustomerName the
/// customer whatever its roles (Vector printed it only with a shipper / consignee flag, a data defect); ETD and Terminal
/// the vessel call's; CommodityDesc master data's description; the boxes the ones holding a place on the booking (not
/// ended, or completed) in line and entry order; CompanyName the branch's invoicing company (the letterhead).
/// </summary>
internal sealed record BookingForm(
    string OrderNo, string? BookingBLNo, string? SubBookingBLNo, string? AgentName, string? CustomerName,
    string? VesselName, string? VoyageNo, DateTime? ETD, string? Terminal, string? MarksAndNos, string? TotalQty, string? UOM,
    decimal? TotalVolume, decimal? TotalWeight, string? CommodityDesc, IReadOnlyList<BookingFormBox> Boxes, string? CompanyName)
{
    /// <summary>COUNT(ContainerNo): every line, nominated or not.</summary>
    public int ContainerCount => Boxes.Count;

    /// <summary>
    /// Vector's line number, RunningValue(ContainerNo, CountDistinct): the count of distinct numbers so far, so an
    /// un-nominated place (no number, '') gets one number the first time and repeats it after, as on Vector.
    /// </summary>
    public IReadOnlyList<int> LineNumbers
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Boxes.Select(b => { seen.Add(b.ContainerNo); return seen.Count; }).ToList();
        }
    }

    /// <summary>The list line: "n. CONTAINER SIZE TYPE".</summary>
    public IReadOnlyList<string> BoxLines => Boxes.Zip(LineNumbers, (b, n) => $"{n}. {b.ContainerNo} {b.Size} {b.Type}").ToList();

    public static async Task<BookingForm?> LoadAsync(TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, Guid bookingId, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(x => x.BookingId == bookingId && x.DeletedAt == null, ct);
        if (b is null) return null;
        var call = b.VesselCallId is { } callId ? await db.VesselCalls.AsNoTracking().SingleOrDefaultAsync(c => c.VesselCallId == callId, ct) : null;
        var line = b.VesselCallLineId is { } lineId ? await db.VesselCallLines.AsNoTracking().SingleOrDefaultAsync(l => l.VesselCallLineId == lineId, ct) : null;
        var boxes = await (
                from x in db.BookingContainers.AsNoTracking()
                where x.BookingId == bookingId && x.DeletedAt == null && (x.EndedAt == null || x.EndReason == "COMPLETED")
                join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
                orderby r.LineNo, x.CreatedAt, x.ContainerNo
                select new { x.ContainerNo, r.EquipmentTypeCode })
            .ToListAsync(ct);
        var types = await TypesAsync(master, boxes.Select(x => (string?)x.EquipmentTypeCode), ct);
        var partyCodes = new[] { b.AgentPartyCode ?? b.LinePartyCode, b.CustomerPartyCode }.OfType<string>().Distinct().ToList();
        var parties = await master.PartiesAsync(partyCodes, ct);
        var vessel = call is null ? null : (await master.VesselsAsync([call.VesselCode], ct)).GetValueOrDefault(call.VesselCode)?.VesselName ?? call.VesselCode;
        var commodity = b.CommodityCode is { } cc ? (await master.CommoditiesAsync([cc], ct)).GetValueOrDefault(cc)?.Description ?? cc : null;
        var company = await master.InvoicingCompanyAsync(b.BranchId, ct);

        return new BookingForm(
            b.OrderNo, BookingBlNo(b.CarrierRef, b.CustomerRef), b.SubBlNo,
            parties.GetValueOrDefault(b.AgentPartyCode ?? b.LinePartyCode)?.Name,
            b.CustomerPartyCode is { } customer ? parties.GetValueOrDefault(customer)?.Name : null,
            vessel,
            Voyage(b.DirectionCode, line?.VoyageIn ?? call?.OperatorVoyageIn, line?.VoyageOut ?? call?.OperatorVoyageOut),
            call is null ? null : Local(branch, call.Etd), call?.TerminalCode, b.MarksAndNos, b.TotalQty?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            b.UomCode, b.TotalVolumeCbm, b.TotalWeightKg, commodity,
            boxes.Select(x =>
            {
                var (size, type) = SizeType(types, x.EquipmentTypeCode);
                return new BookingFormBox(x.ContainerNo ?? "", size, type);
            }).ToList(),
            company is null ? null : company.LegalNameLocal ?? company.LegalNameEn);
    }
}
