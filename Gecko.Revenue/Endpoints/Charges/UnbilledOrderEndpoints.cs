using System.Globalization;
using ClosedXML.Excel;
using Gecko.Data;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Gecko.Tos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Charges;

/// <summary>
/// Vector's Unbilled Orders screen (Billing/UnBilledOrders.cs, Operation.usp_RetrieveUnbilledOrders /
/// usp_RetrieveUnbilledCharges), on Gecko's data. An unbilled line there is a statement row with nothing paid
/// on a movement that has happened; here it is a <c>billing.charge</c> in status UNBILLED — the credit lines
/// the gate priced when the box moved (and cash lines a haulier's term put on credit). Cash is collected
/// before the barrier, so a moved box owes no unpaid cash.
///
/// Three reads, one filter set: the orders (top grid), their lines (bottom grid), and the printouts — the screen's
/// workbook (orders.xlsx) and Vector's TMS.Billing.UnBilled.UnBilledOrderCharges report (charges.pdf / charges.xlsx).
/// The booking's B/L, vessel, voyage and progress come from TOS (<see cref="ITosBookingHeaders"/>), its owner.
/// </summary>
internal static class UnbilledOrderEndpoints
{
    public static readonly string[] Progresses = ["ALL", "COMPLETED", "HALF_COMPLETED", "DELIVERED"];
    private static readonly string[] BillTos = ["AGENT", "CUSTOMER", "FORWARDER", "LINE", "HAULIER"];
    private const int MaxLines = 20_000;
    private const string CreditTerm = "CREDIT";

    public static RouteGroupBuilder MapUnbilledOrderEndpoints(this RouteGroupBuilder revenue)
    {
        var unbilled = revenue.MapGroup("/charges/unbilled").WithTags("Revenue — charges");
        unbilled.MapGet("/orders", OrdersAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Unbilled orders (Vector UnBilledOrders): one row per booking with unbilled lines, the desktop's filters");
        unbilled.MapGet("/lines", LinesAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The unbilled lines of one or more orders (orderNo repeats), with the same filters");
        unbilled.MapGet("/orders.xlsx", ExportAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The unbilled orders and their lines as a workbook");
        unbilled.MapGet("/charges.{format}", ChargesReportAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("UNBILLED ORDER CHARGES REPORT (Vector TMS.Billing.UnBilled.UnBilledOrderCharges): a row per unbilled line, as PDF or Excel")
            .WithDescription("The same filters as /orders (Vector's Filter = progress).");
        return revenue;
    }

    private static async Task<Results<Ok<UnbilledOrdersPage>, ValidationProblem, ForbidHttpResult>> OrdersAsync(
        [AsParameters] UnbilledFilter filter, RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct, int? page = 1, int? pageSize = 50)
    {
        var (found, problem) = await FindAsync(filter, db, tos, scope, ct);
        if (problem is not null) return problem.Result is ValidationProblem v ? v : TypedResults.Forbid();

        var orders = await OrdersOfAsync(found!, master, ct);
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        return TypedResults.Ok(new UnbilledOrdersPage(
            orders.Skip((p - 1) * size).Take(size).ToList(), p, size, orders.Count,
            found!.Lines.Count(IsOpen), Unbilled(found.Lines).Sum(l => l.Amount),
            Unbilled(found.Lines).Sum(l => l.TaxAmount), Unbilled(found.Lines).Sum(l => l.Amount + l.TaxAmount)));
    }

    private static async Task<Results<Ok<IReadOnlyList<UnbilledLineResponse>>, ValidationProblem, ForbidHttpResult>> LinesAsync(
        [AsParameters] UnbilledFilter filter, RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        if (filter.OrderNo is not { Length: > 0 }) return RevenueSupport.Invalid("orderNo", "Which orders? Repeat orderNo for each ticked order.");
        var (found, problem) = await FindAsync(filter, db, tos, scope, ct);
        if (problem is not null) return problem.Result is ValidationProblem v ? v : TypedResults.Forbid();
        return TypedResults.Ok(await LinesOfAsync(found!, db, master, ct));
    }

    private static async Task<Results<FileContentHttpResult, ValidationProblem, ForbidHttpResult>> ExportAsync(
        [AsParameters] UnbilledFilter filter, RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master,
        ICallerPermissions scope, TimeProvider clock, CancellationToken ct)
    {
        var (found, problem) = await FindAsync(filter, db, tos, scope, ct);
        if (problem is not null) return problem.Result is ValidationProblem v ? v : TypedResults.Forbid();
        var orders = await OrdersOfAsync(found!, master, ct);
        var lines = await LinesOfAsync(found!, db, master, ct);

        using var book = new XLWorkbook();
        var o = book.Worksheets.Add("Unbilled orders");
        string[] oh = ["Order no", "B/L", "Sub-B/L", "Booked", "Booking type", "Order type", "Agent", "Customer", "Forwarder",
            "Vessel", "Voyage", "Wharf", "Payment term", "Billed to", "Boxes", "Lines", "Amount", "VAT", "Total", "Steps done"];
        for (var i = 0; i < oh.Length; i++) o.Cell(1, i + 1).Value = oh[i];
        var r = 2;
        foreach (var x in orders)
        {
            object?[] v = [x.OrderNo, x.CarrierRef, x.SubBlNo, x.BookedAt.ToString("yyyy-MM-dd"), x.BookingTypeCode, x.OrderTypeCode,
                x.AgentName ?? x.AgentCode, x.CustomerName ?? x.CustomerCode, x.ForwarderCode, x.VesselCode, x.Voyage, x.TerminalCode,
                string.Join(", ", x.PaymentTerms), string.Join(", ", x.BillTo), x.Boxes, x.Lines, x.Amount, x.Tax, x.Total, $"{x.StepsDone}/{x.StepsTotal}"];
            for (var i = 0; i < v.Length; i++) o.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
            r++;
        }
        var c = book.Worksheets.Add("Unbilled charges");
        string[] ch = ["Order no", "Container", "Type", "Movement", "EIR", "Date", "Charge", "Description", "Billed to", "Payer",
            "Payment term", "Qty", "Rate", "Amount", "VAT %", "VAT", "Total"];
        for (var i = 0; i < ch.Length; i++) c.Cell(1, i + 1).Value = ch[i];
        r = 2;
        foreach (var x in lines)
        {
            object?[] v = [x.OrderNo, x.ContainerNo, x.EquipmentTypeCode, x.MovementCode, x.EirNo, x.PricedForDate?.ToString("yyyy-MM-dd"),
                x.ChargeCode, x.ChargeName, x.BillTo, x.PayerName ?? x.PayerCode, x.PaymentTermCode, x.Quantity, x.UnitRate, x.Amount,
                x.TaxRate, x.TaxAmount, x.Total];
            for (var i = 0; i < v.Length; i++) c.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
            r++;
        }
        foreach (var sheet in book.Worksheets) { sheet.Row(1).Style.Font.Bold = true; sheet.Columns().AdjustToContents(); }

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return TypedResults.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"unbilled-orders-{clock.GetUtcNow():yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>Vector's LookupCategory 'PaymentTo' descriptions, by Gecko's bill-to role.</summary>
    private static readonly IReadOnlyDictionary<string, string> PaymentTo = new Dictionary<string, string>
    {
        ["AGENT"] = "AGENT", ["CUSTOMER"] = "SHIPPER/CONSIGNEE", ["FORWARDER"] = "FORWARDING AGENT", ["HAULIER"] = "HAULIER", ["LINE"] = "SHIPPING",
    };

    /// <summary>
    /// TMS.Billing.UnBilled.UnBilledOrderCharges (Report.usp_UnbilledOrdersCharges): the branch, "&lt;&lt; UNBILLED ORDER CHARGES
    /// REPORT &gt;&gt;", Print Date; a row per unbilled line in order-no order with the RDL's 18 columns; Page No. — A4 landscape.
    /// Size/Type are the box's booked equipment type, Billing Unit lookup.billing_unit's name, Payment To Vector's words for the role.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ValidationProblem, ForbidHttpResult, NotFound>> ChargesReportAsync(
        string format, [AsParameters] UnbilledFilter filter, RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master,
        IUserDirectory users, BranchCalendar calendar, ICallerPermissions scope, CancellationToken ct)
    {
        if (format is not ("pdf" or "xlsx")) return TypedResults.NotFound();
        var (found, problem) = await FindAsync(filter with { IncludeSettled = false }, db, tos, scope, ct);
        if (problem is not null) return problem.Result is ValidationProblem v ? v : TypedResults.Forbid();

        var lines = found!.Lines.OrderBy(l => found.Headers[l.BookingId!.Value].OrderNo, StringComparer.Ordinal)
            .ThenBy(l => l.ContainerNo).ThenBy(l => l.ChargeCode).ToList();
        var boxIds = lines.Select(l => l.BookingContainerId).OfType<Guid>().Distinct().ToList();
        var boxTypes = await db.BookingPlanContainers.AsNoTracking().Where(b => boxIds.Contains(b.BookingContainerId))
            .ToDictionaryAsync(b => b.BookingContainerId, b => b.EquipmentTypeCode, ct);
        var typeCodes = boxTypes.Values.OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var units = await db.BillingUnits.AsNoTracking().ToDictionaryAsync(u => u.Code, u => u.DescriptionEn, ct);
        var branchId = filter.BranchId!.Value;
        var branchName = (await users.BranchNamesAsync([branchId], ct)).GetValueOrDefault(branchId)
                         ?? (await calendar.BranchAsync(branchId, ct))?.BranchCode ?? "";
        var printed = (await calendar.BranchAsync(branchId, ct)) is { } clock ? clock.Local(calendar.Now) : calendar.Now;

        var rows = lines.Select(l =>
        {
            var h = found.Headers[l.BookingId!.Value];
            var (size, type) = Application.Reports.OperationChargeReports.SizeType(types,
                l.BookingContainerId is { } box ? boxTypes.GetValueOrDefault(box) : null);
            return new TabularRow([h.CarrierRef, h.SubBlNo, h.BookingTypeCode, h.OrderTypeCode, h.AgentCode, h.CustomerCode, h.VesselCode,
                h.Voyage, l.ContainerNo, l.ChargeCode, l.MovementCode,
                l.BillingUnitCode is { } u ? units.GetValueOrDefault(u) ?? u : null,
                l.UnitRate, l.Quantity / 1.000000000000000000000000000000000m, size, type, l.PaymentTermCode, PaymentTo.GetValueOrDefault(l.BillTo) ?? l.BillTo]);
        }).ToList();

        string[] headers = ["Booking BLNo", "Sub Booking BLNo", "Booking Type", "Order Type", "Agent Code", "Customer Code", "Vessel",
            "Voyage No", "Container No", "Charge Code", "Movement Code", "Billing Unit", "Price", "Qty", "Size", "Type", "Payment Term", "Payment To"];
        double[] widthsIn = [1.16667, 1, 0.73041, 0.84375, 0.63542, 0.92708, 0.46875, 0.56944, 0.79167, 1.30208, 0.80717, 0.63542, 0.51042,
            0.32292, 0.34375, 0.36458, 0.625, 0.75];
        var report = new TabularReport(
            FileName: $"UnbilledOrderCharges_{branchName.Replace(' ', '_')}_{printed:yyyyMMdd-HHmm}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: [new(branchName, 10, Bold: true), new("<< UNBILLED ORDER CHARGES REPORT >>", 10, Bold: true)],
            HeadingRight: [new($"Print Date: {printed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}")],
            Preamble: [],
            PreambleRight: [],
            Columns: widthsIn.Select((w, i) => new TabularColumn(w * 2.54, i == 12 ? "0.00" : null,
                i is 12 or 13 ? CellAlign.Right : CellAlign.Left)).ToList(),
            HeaderRows: [headers.Select(x => new HeaderCell(x)).ToList()],
            Rows: rows,
            PageLabel: "Page No.");
        return format == "pdf"
            ? TypedResults.File(report.Pdf(), TabularReport.PdfType, report.FileName + ".pdf")
            : TypedResults.File(report.Xlsx(), TabularReport.XlsxType, report.FileName + ".xlsx");
    }

    // ── the one filter ─────────────────────────────────────────────────────

    /// <param name="Empty">With includeSettled and no line filter: the latest bookings that have no billable line yet
    /// (owner 2026-10-07 — the register lists bookings, so a clerk can open one and add a manual charge).</param>
    private sealed record Found(List<Charge> Lines, IReadOnlyDictionary<Guid, TosBookingHeader> Headers, List<TosBookingHeader> Empty);

    /// <summary>How many of the latest bookings without a billable line the register looks at.</summary>
    private const int LatestBookings = 500;

    private sealed record Problem(IResult Result);

    private static async Task<(Found? Found, Problem? Problem)> FindAsync(
        UnbilledFilter f, RevenueDbContext db, ITosBookingHeaders tos, ICallerPermissions scope, CancellationToken ct)
    {
        if (f.BranchId is not { } branch) return (null, new(RevenueSupport.Invalid("branchId", "Which depot?")));
        if (!scope.HasAt(RevenuePermissions.ChargeView, branch)) return (null, new(TypedResults.Forbid()));
        var progress = (f.Progress ?? "ALL").Trim().ToUpperInvariant();
        if (!Progresses.Contains(progress))
            return (null, new(RevenueSupport.Invalid("progress", $"Use one of: {string.Join(", ", Progresses)}.")));
        if (f.From is { } from && f.To is { } to && to < from) return (null, new(RevenueSupport.Invalid("to", "The end is before the start.")));
        static string? Up(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
        var billTo = Up(f.BillTo);
        if (billTo is not null && !BillTos.Contains(billTo))
            return (null, new(RevenueSupport.Invalid("billTo", $"Use one of: {string.Join(", ", BillTos)}.")));

        // Vector's rule (usp_RetrieveUnbilledCharges): naming ONE party picks the lines billed to that role.
        var (agent, forwarder, customer) = (Up(f.AgentCode), Up(f.ForwarderCode), Up(f.CustomerCode));
        billTo ??= (agent, forwarder, customer) switch
        {
            ({ }, { }, { }) => null,
            ({ }, _, _) => "AGENT",
            (_, { }, _) => "FORWARDER",
            (_, _, { }) => "CUSTOMER",
            _ => null,
        };

        // includeSettled (owner 2026-10-07): a booking billed or paid in full has nothing UNBILLED and would
        // never be listed; with the flag every billable line counts — unbilled, invoiced, paid, earned.
        string[] statuses = f.IncludeSettled == true
            ? [ChargeStatus.Unbilled, ChargeStatus.Invoiced, ChargeStatus.Paid, ChargeStatus.Earned]
            : [ChargeStatus.Unbilled];
        // A CREDIT line is billable from the moment the booking is priced (owner 2026-10-07: an invoice may
        // bill QUOTED credit lines in advance; 2026-10-19: they belong on this worklist). A QUOTED cash line is
        // not — it is paid at the window when the box moves. Unpriced placeholders (0, no tariff) are left out.
        var rows = db.Charges.AsNoTracking()
            .Where(c => c.BranchId == branch && c.BookingId != null
                        && (statuses.Contains(c.Status)
                            || (c.Status == ChargeStatus.Quoted && c.PaymentTermCode == CreditTerm
                                && (c.Amount != 0 || c.Source == ChargeSource.Manual))));
        if (billTo is not null) rows = rows.Where(c => c.BillTo == billTo);
        if (Up(f.PaymentTermCode) is { } term) rows = rows.Where(c => c.PaymentTermCode == term);
        if (Up(f.ChargeCode) is { } charge) rows = rows.Where(c => c.ChargeCode == charge);
        if (Up(f.MovementCode) is { } movement) rows = rows.Where(c => c.MovementCode == movement);
        if (f.From is { } start) rows = rows.Where(c => c.PricedForDate >= start);
        if (f.To is { } end) rows = rows.Where(c => c.PricedForDate <= end);
        if (f.OrderNo is { Length: > 0 } orders)
        {
            var wanted = orders.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
            rows = rows.Where(c => wanted.Contains(c.OrderNo!));
        }
        var lines = await rows.OrderBy(c => c.OrderNo).ThenBy(c => c.ContainerNo).ThenBy(c => c.ChargeCode).Take(MaxLines).ToListAsync(ct);

        var headers = await tos.HeadersAsync(lines.Select(l => l.BookingId!.Value).Distinct().ToList(), ct);
        bool Keep(TosBookingHeader h) =>
            h.Status != "CANCELLED"
            && (agent is null || h.AgentCode == agent)
            && (forwarder is null || h.ForwarderCode == forwarder)
            && (customer is null || h.CustomerCode == customer)
            && (Up(f.BookingTypeCode) is not { } bt || h.BookingTypeCode == bt)
            && (Up(f.OrderTypeCode) is not { } ot || h.OrderTypeCode == ot)
            && (string.IsNullOrWhiteSpace(f.CarrierRef) || (h.CarrierRef ?? "").Contains(f.CarrierRef.Trim(), StringComparison.OrdinalIgnoreCase))
            && (Up(f.VesselCode) is not { } vc || h.VesselCode == vc)
            && (string.IsNullOrWhiteSpace(f.Voyage) || string.Equals(h.Voyage, f.Voyage.Trim(), StringComparison.OrdinalIgnoreCase))
            // 50% COMPLETED (Vector): at least half the order's movements done.
            && (progress != "HALF_COMPLETED" || h.StepsDone * 2 >= h.StepsTotal);
        lines = lines.Where(l => headers.TryGetValue(l.BookingId!.Value, out var h) && Keep(h)
                                 // COMPLETED (Vector): only the boxes with no movement left.
                                 && (progress != "COMPLETED" || (l.BookingContainerId is { } box && h.CompletedBoxes.Contains(box))))
            .ToList();

        // Every booking, not only the ones with money on them — unless a filter is about the lines themselves.
        var empty = new List<TosBookingHeader>();
        var lineFilter = billTo is not null || Up(f.PaymentTermCode) is not null || Up(f.ChargeCode) is not null
                         || Up(f.MovementCode) is not null || f.From is not null || f.To is not null || progress == "COMPLETED";
        if (f.IncludeSettled == true && !lineFilter)
        {
            var have = lines.Select(l => l.BookingId!.Value).ToHashSet();
            var latest = db.BookingPlans.AsNoTracking().Where(p => p.BranchId == branch && p.Status != "CANCELLED");
            if (f.OrderNo is { Length: > 0 } named)
            {
                var wanted = named.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                latest = latest.Where(p => wanted.Contains(p.OrderNo));
            }
            var ids = (await latest.OrderByDescending(p => p.OrderNo).Select(p => p.BookingId).Take(LatestBookings).ToListAsync(ct))
                .Where(id => !have.Contains(id)).ToList();
            var more = await tos.HeadersAsync(ids, ct);
            empty = more.Values.Where(Keep).ToList();
            headers = headers.Concat(more.Where(m => !headers.ContainsKey(m.Key))).ToDictionary(m => m.Key, m => m.Value);
        }
        return (new Found(lines, headers, empty), null);
    }

    private static async Task<List<UnbilledOrderResponse>> OrdersOfAsync(Found found, IMasterDataReferences master, CancellationToken ct)
    {
        var groups = found.Lines.GroupBy(l => l.BookingId!.Value).ToList();
        var codes = groups.Select(g => found.Headers[g.Key]).Concat(found.Empty)
            .SelectMany(h => new[] { h.AgentCode, h.CustomerCode }).OfType<string>().Distinct();
        var parties = await master.PartiesAsync(codes, ct);
        // A booking with no billable line yet: listed with zeros, to be opened (and charged by hand).
        var empty = found.Empty.Select(h => new UnbilledOrderResponse(
            h.BookingId, h.OrderNo, h.CarrierRef, h.SubBlNo, h.BookedAt, h.BookingTypeCode, h.OrderTypeCode,
            h.AgentCode, h.AgentCode is { } a ? parties.GetValueOrDefault(a)?.Name : null,
            h.CustomerCode, h.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, h.ForwarderCode,
            h.VesselCode, h.CallRef, h.Voyage, h.TerminalCode, [], [], 0, 0, 0m, 0m, 0m, "THB", h.StepsDone, h.StepsTotal, null, null,
            TotalBillable: 0m, BilledAmount: 0m, UnbilledAmount: 0m));
        return groups.Select(g =>
            {
                var h = found.Headers[g.Key];
                var all = g.ToList();
                var list = Unbilled(all).ToList();
                if (list.Count == 0) list = all;   // settled in full: the row still shows its lines and currency
                var billed = all.Where(l => !IsOpen(l)).Sum(l => l.Amount + l.TaxAmount);
                var unbilled = Unbilled(all).Sum(l => l.Amount + l.TaxAmount);
                return new UnbilledOrderResponse(
                    h.BookingId, h.OrderNo, h.CarrierRef, h.SubBlNo, h.BookedAt, h.BookingTypeCode, h.OrderTypeCode,
                    h.AgentCode, h.AgentCode is { } a ? parties.GetValueOrDefault(a)?.Name : null,
                    h.CustomerCode, h.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, h.ForwarderCode,
                    h.VesselCode, h.CallRef, h.Voyage, h.TerminalCode,
                    list.Select(l => l.PaymentTermCode).Distinct().Order().ToList(), list.Select(l => l.BillTo).Distinct().Order().ToList(),
                    list.Select(l => l.BookingContainerId).Distinct().Count(), list.Count,
                    Unbilled(all).Sum(l => l.Amount), Unbilled(all).Sum(l => l.TaxAmount), unbilled,
                    list.Select(l => l.CurrencyCode).First(), h.StepsDone, h.StepsTotal,
                    list.Min(l => l.PricedForDate), list.Max(l => l.PricedForDate),
                    TotalBillable: billed + unbilled, BilledAmount: billed, UnbilledAmount: unbilled);
            })
            .Concat(empty)
            // Newest first (owner 2026-10-07): page 1 is the latest 50 bookings.
            .OrderByDescending(o => o.BookedAt).ThenByDescending(o => o.OrderNo, StringComparer.Ordinal).ToList();
    }

    /// <summary>Still to be billed: moved (UNBILLED), or a priced credit line of a booked box (QUOTED) — see FindAsync.</summary>
    private static bool IsOpen(Charge l) => l.Status is ChargeStatus.Unbilled or ChargeStatus.Quoted;

    private static IEnumerable<Charge> Unbilled(IEnumerable<Charge> lines) => lines.Where(IsOpen);

    private static async Task<IReadOnlyList<UnbilledLineResponse>> LinesOfAsync(Found found, RevenueDbContext db, IMasterDataReferences master, CancellationToken ct)
    {
        var boxIds = found.Lines.Select(l => l.BookingContainerId).OfType<Guid>().Distinct().ToList();
        var types = await db.BookingPlanContainers.AsNoTracking().Where(b => boxIds.Contains(b.BookingContainerId))
            .ToDictionaryAsync(b => b.BookingContainerId, b => b.EquipmentTypeCode, ct);
        var parties = await master.PartiesAsync(found.Lines.Select(l => l.PayerPartyCode).OfType<string>().Distinct(), ct);
        return found.Lines.Select(l => new UnbilledLineResponse(
            l.ChargeId, l.BookingId!.Value, l.OrderNo ?? found.Headers[l.BookingId!.Value].OrderNo, l.BookingContainerId, l.ContainerNo,
            l.BookingContainerId is { } b ? types.GetValueOrDefault(b) : null, l.MovementCode, l.EirNo, l.PricedForDate,
            l.ChargeCode, l.ChargeName, l.BillTo, l.PayerPartyCode, l.PayerPartyCode is { } p ? parties.GetValueOrDefault(p)?.Name : null,
            l.PaymentTermCode, l.Quantity, l.UnitRate, l.Amount, l.TaxRate, l.TaxAmount, l.Amount + l.TaxAmount, l.CurrencyCode,
            l.BillingUnitCode, l.IsTripCharge, l.ScheduleNo, l.Source, l.Status)).ToList();
    }
}

/// <summary>
/// Vector's filters. <see cref="BillTo"/> null with exactly one of agent / forwarder / customer named = the lines
/// billed to that role (as Vector did). <see cref="Progress"/>: ALL (any moved box; DELIVERED is the same, as on
/// Vector), COMPLETED (only boxes with no movement left), HALF_COMPLETED (orders with at least half their
/// movements done). <see cref="From"/> / <see cref="To"/>: the day the charge was priced for (the move's date).
/// </summary>
public sealed record UnbilledFilter
{
    public Guid? BranchId { get; init; }
    public string? AgentCode { get; init; }
    public string? ForwarderCode { get; init; }
    public string? CustomerCode { get; init; }
    public string? BillTo { get; init; }
    public string? VesselCode { get; init; }
    public string? Voyage { get; init; }
    public string? BookingTypeCode { get; init; }
    public string? OrderTypeCode { get; init; }
    public string? CarrierRef { get; init; }
    public string? MovementCode { get; init; }
    public string? PaymentTermCode { get; init; }
    public string? ChargeCode { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Progress { get; init; }
    public string[]? OrderNo { get; init; }
    /// <summary>true: bookings billed or paid in full are listed too, with TotalBillable / BilledAmount / UnbilledAmount.</summary>
    public bool? IncludeSettled { get; init; }
}

public sealed record UnbilledOrdersPage(
    IReadOnlyList<UnbilledOrderResponse> Items, int Page, int PageSize, int TotalCount,
    int Lines, decimal Amount, decimal Tax, decimal Total);

public sealed record UnbilledOrderResponse(
    Guid BookingId, string OrderNo, string? CarrierRef, string? SubBlNo, DateTimeOffset BookedAt,
    string BookingTypeCode, string OrderTypeCode, string? AgentCode, string? AgentName, string? CustomerCode, string? CustomerName,
    string? ForwarderCode, string? VesselCode, string? CallRef, string? Voyage, string? TerminalCode,
    IReadOnlyList<string> PaymentTerms, IReadOnlyList<string> BillTo,
    int Boxes, int Lines, decimal Amount, decimal Tax, decimal Total, string CurrencyCode,
    int StepsDone, int StepsTotal, DateOnly? FirstDate, DateOnly? LastDate,
    decimal? TotalBillable = null, decimal? BilledAmount = null, decimal? UnbilledAmount = null);

public sealed record UnbilledLineResponse(
    Guid ChargeId, Guid BookingId, string OrderNo, Guid? BookingContainerId, string? ContainerNo, string? EquipmentTypeCode,
    string? MovementCode, string? EirNo, DateOnly? PricedForDate,
    string ChargeCode, string? ChargeName, string BillTo, string? PayerCode, string? PayerName, string PaymentTermCode,
    decimal Quantity, decimal? UnitRate, decimal Amount, decimal TaxRate, decimal TaxAmount, decimal Total, string CurrencyCode,
    string? BillingUnitCode, bool IsTripCharge, string? ScheduleNo, string Source,
    /// <summary>UNBILLED = the box has moved; QUOTED = a credit line of a booked box, billable in advance.</summary>
    string Status);
