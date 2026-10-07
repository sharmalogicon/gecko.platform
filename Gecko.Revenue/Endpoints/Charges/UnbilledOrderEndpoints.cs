using ClosedXML.Excel;
using Gecko.Data;
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
/// Three reads, one filter set: the orders (top grid), their lines (bottom grid), and the printout (xlsx).
/// The booking's B/L, vessel, voyage and progress come from TOS (<see cref="ITosBookingHeaders"/>), its owner.
/// </summary>
internal static class UnbilledOrderEndpoints
{
    public static readonly string[] Progresses = ["ALL", "COMPLETED", "HALF_COMPLETED", "DELIVERED"];
    private static readonly string[] BillTos = ["AGENT", "CUSTOMER", "FORWARDER", "LINE", "HAULIER"];
    private const int MaxLines = 20_000;

    public static RouteGroupBuilder MapUnbilledOrderEndpoints(this RouteGroupBuilder revenue)
    {
        var unbilled = revenue.MapGroup("/charges/unbilled").WithTags("Revenue — charges");
        unbilled.MapGet("/orders", OrdersAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Unbilled orders (Vector UnBilledOrders): one row per booking with unbilled lines, the desktop's filters");
        unbilled.MapGet("/lines", LinesAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The unbilled lines of one or more orders (orderNo repeats), with the same filters");
        unbilled.MapGet("/orders.xlsx", ExportAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The unbilled orders and their lines as a workbook (Vector's Unbilled Order Charges report)");
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
            found!.Lines.Count(l => l.Status == ChargeStatus.Unbilled), Unbilled(found.Lines).Sum(l => l.Amount),
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

    // ── the one filter ─────────────────────────────────────────────────────

    private sealed record Found(List<Charge> Lines, IReadOnlyDictionary<Guid, TosBookingHeader> Headers);

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
        var rows = db.Charges.AsNoTracking()
            .Where(c => c.BranchId == branch && statuses.Contains(c.Status) && c.BookingId != null);
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
        return (new Found(lines, headers), null);
    }

    private static async Task<List<UnbilledOrderResponse>> OrdersOfAsync(Found found, IMasterDataReferences master, CancellationToken ct)
    {
        var groups = found.Lines.GroupBy(l => l.BookingId!.Value).ToList();
        var codes = groups.Select(g => found.Headers[g.Key]).SelectMany(h => new[] { h.AgentCode, h.CustomerCode }).OfType<string>().Distinct();
        var parties = await master.PartiesAsync(codes, ct);
        return groups.Select(g =>
            {
                var h = found.Headers[g.Key];
                var all = g.ToList();
                var list = Unbilled(all).ToList();
                if (list.Count == 0) list = all;   // settled in full: the row still shows its lines and currency
                var billed = all.Where(l => l.Status != ChargeStatus.Unbilled).Sum(l => l.Amount + l.TaxAmount);
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
            .OrderBy(o => o.OrderNo, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Charge> Unbilled(IEnumerable<Charge> lines) => lines.Where(l => l.Status == ChargeStatus.Unbilled);

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
            l.BillingUnitCode, l.IsTripCharge, l.ScheduleNo, l.Source)).ToList();
    }
}

/// <summary>
/// Vector's filters. <see cref="BillTo"/> null with exactly one of agent / forwarder / customer named = the lines
/// billed to that role (as Vector did). <see cref="Progress"/>: ALL (any moved box; DELIVERED is the same, as on
/// Vector), COMPLETED (only boxes with no movement left), HALF_COMPLETED (orders with at least half their
/// movements done). <see cref="From"/> / <see cref="To"/>: the day the charge was priced for (the move's date).
/// </summary>
public sealed class UnbilledFilter
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
    string? BillingUnitCode, bool IsTripCharge, string? ScheduleNo, string Source);
