using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// Split a gate receipt (owner 2026-10-08): after the gate Save the truck's one receipt is made out
/// to several payers — some charges to another customer, the rest to the haulier. The money taken
/// does not change; the tax invoices do.
///   * every line goes to exactly one part;
///   * ONE part is a change of payer (owner 2026-10-09): any issued receipt, any day, keeps its number —
///     the payer columns are updated in place, nothing else changes, nothing is voided;
///   * two or more parts: gate receipts only (issued_from GATE), ISSUED, and only on the day they were issued.
///     The FIRST part keeps the original receipt and number (its lines, payments and totals re-stated);
///     every other part is a NEW receipt (CA + branch + YYMM + 5) naming it (split_from_receipt_id);
///   * coupons are NOT withdrawn and the charges keep their status (the boxes have moved): they are
///     re-pointed to their part, with the part's payer;
///   * each part's payments (channels as the clerk chooses) add up to its own nett, and per
///     channel the parts together take exactly what the original took — the drawer does not change;
///   * withholding tax only on a part over ฿1,000 (3% of its amount before VAT).
/// For now any user who may take cash may split (owner: the void permission later).
/// </summary>
internal static class ReceiptSplitEndpoints
{
    private static readonly string[] Channels = ["CASH", "TRANSFER", "CHEQUE", "CARD"];

    public static RouteGroupBuilder MapReceiptSplitEndpoints(this RouteGroupBuilder revenue)
    {
        revenue.MapGroup("/window/receipts").WithTags("Revenue — cash window")
            .MapPost("/{id:guid}/split", SplitAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Change who a receipt is made out to (one part, keeps its number), or split a gate receipt of today among several payers (the first part keeps the number); the money is unchanged");
        return revenue;
    }

    private static async Task<Results<Ok<IReadOnlyList<ReceiptResponse>>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> SplitAsync(
        Guid id, SplitReceiptRequest request, RevenueDbContext db, ReceiptDocument document, BranchCalendar calendar,
        IMasterDataReferences master, ITenantContext caller, ICallerPermissions permissions, CancellationToken ct)
    {
        var original = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptId == id, ct);
        if (original is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such receipt." });
        if (!permissions.HasAt(RevenuePermissions.CashCollect, original.BranchId)) return TypedResults.Forbid();
        if (original.Status != "ISSUED") return RevenueSupport.Conflict($"{original.ReceiptNo} is {original.Status}; only an issued receipt is split.");
        var branch = await calendar.BranchAsync(original.BranchId, ct);
        if (branch is null) return RevenueSupport.Conflict("This depot has no master-data profile.");
        var now = calendar.Now;
        // One part is a change of payer: any issued receipt, any day (owner 2026-10-09). Dividing one stays a gate, same-day thing.
        var renaming = request.Parts is { Count: 1 };
        if (!renaming && original.IssuedFrom != "GATE") return RevenueSupport.Conflict($"{original.ReceiptNo} was not issued at the gate; only a gate receipt is split.");
        if (!renaming && branch.LocalDate(original.ReceiptAt) != branch.LocalDate(now))
            return RevenueSupport.Conflict($"{original.ReceiptNo} was issued on {branch.LocalDate(original.ReceiptAt):dd-MM-yyyy}; a receipt is split on the day it was issued only.");

        var lines = await db.ReceiptLines.AsNoTracking().Where(l => l.ReceiptId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        var paidBefore = await db.ReceiptPayments.AsNoTracking().Where(p => p.ReceiptId == id).ToListAsync(ct);

        // ── the parts ──────────────────────────────────────────────────────────
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var list)) errors[key] = list = []; list.Add(message); }
        var parts = request.Parts ?? [];
        if (parts.Count < 1) Add("parts", "A split needs a part at least.");   // one part = re-issue to another payer
        var taken = parts.SelectMany(p => p.LineNos ?? []).ToList();
        if (taken.Count != taken.Distinct().Count()) Add("parts", "A line is in two parts.");
        var known = lines.Select(l => l.LineNo).ToHashSet();
        if (taken.Any(n => !known.Contains(n))) Add("parts", $"{original.ReceiptNo} has lines {string.Join(", ", known)} only.");
        if (known.Any(n => !taken.Contains(n))) Add("parts", "Every line goes to one part.");

        var planned = new List<(SplitPartRequest Part, List<ReceiptLine> Lines, decimal Subtotal, decimal Tax, decimal Withheld, string? PartyCode)>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var key = $"parts[{i}]";
            var mine = lines.Where(l => (part.LineNos ?? []).Contains(l.LineNo)).ToList();
            if (mine.Count == 0) { Add(key, "A part has a line at least."); continue; }
            var subtotal = mine.Sum(l => l.Amount);
            var tax = mine.Sum(l => l.TaxAmount);
            var total = subtotal + tax;
            var withheld = 0m;
            if (part.WithholdingTax == true)
            {
                if (!WithholdingTax.MayApply(total)) Add($"{key}.withholdingTax", $"Withholding tax only on a part over ฿{WithholdingTax.Threshold:N0}; this one is ฿{total:N2}.");
                else withheld = WithholdingTax.Amount(subtotal);
            }
            if (part.Payments is not { Count: > 0 }) Add($"{key}.payments", "How was this part paid?");
            foreach (var p in part.Payments ?? [])
            {
                var channel = p.Channel?.Trim().ToUpperInvariant() ?? "";
                if (!Channels.Contains(channel)) Add($"{key}.payments", $"'{p.Channel}' is not a channel ({string.Join(", ", Channels)}).");
                if (p.Amount <= 0) Add($"{key}.payments", "Every payment must be more than zero.");
                if (channel != "CASH" && string.IsNullOrWhiteSpace(p.ReferenceNo)) Add($"{key}.payments", $"A {channel} payment needs its reference.");
            }
            var paid = (part.Payments ?? []).Sum(p => p.Amount);
            if (paid != total - withheld)
                Add($"{key}.payments", withheld > 0
                    ? $"Payments add up to ฿{paid:N2}; this part is ฿{total:N2} less ฿{withheld:N2} withholding tax = ฿{total - withheld:N2}."
                    : $"Payments add up to ฿{paid:N2}; this part is ฿{total:N2}.");
            var partyCode = part.PayerPartyCode?.Trim().ToUpperInvariant();
            planned.Add((part, mine, subtotal, tax, withheld, string.IsNullOrEmpty(partyCode) ? null : partyCode));
        }
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        // The money does not change: per channel, the parts take what the original took.
        var before = paidBefore.GroupBy(p => p.Channel).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var after = planned.SelectMany(p => p.Part.Payments!).GroupBy(p => p.Channel.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        if (before.Keys.Union(after.Keys).Any(c => before.GetValueOrDefault(c) != after.GetValueOrDefault(c)))
            return RevenueSupport.Conflict("The parts must take the same money as the receipt, channel by channel.",
                $"{original.ReceiptNo} took {string.Join(", ", before.Select(b => $"{b.Key} ฿{b.Value:N2}"))}; the parts take "
                + $"{string.Join(", ", after.Select(a => $"{a.Key} ฿{a.Value:N2}"))}. Withholding tax changes what is paid — keep it as on the receipt.");

        var codes = planned.Select(p => p.PartyCode).OfType<string>().Distinct().ToList();
        var parties = await master.PartiesAsync(codes, ct);
        if (codes.FirstOrDefault(c => !parties.ContainsKey(c)) is { } unknown)
            return RevenueSupport.Invalid("parts", $"'{unknown}' is not a party of this tenant.");

        // ── write it ────────────────────────────────────────────────────────────
        // The FIRST part keeps the original receipt and its number (owner 2026-10-09): its payer —
        // and, when the receipt is divided, its lines, payments and totals — are re-stated in place
        // (30_receipt_amend grants). Only the other parts are new receipts naming it. One part is
        // therefore a change of payer: nothing is voided and no number is used.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var chargeIds = lines.Select(l => l.ChargeId).ToList();
        var charges = await db.Charges.Where(c => chargeIds.Contains(c.ChargeId)).ToDictionaryAsync(c => c.ChargeId, ct);
        var me = caller.UserId();

        (Guid? BookingId, string? OrderNo) BookingOf(List<ReceiptLine> mine)
        {
            var bookings = mine.Select(l => charges.GetValueOrDefault(l.ChargeId)).OfType<Charge>().Select(c => (c.BookingId, c.OrderNo)).Distinct().ToList();
            return bookings.Count == 1 ? (bookings[0].BookingId, bookings[0].OrderNo) : (null, null);
        }
        string PayerName(SplitPartRequest part, string? partyCode) => part.Payer?.Name?.Trim() is { Length: > 0 } named ? named
            : partyCode is not null ? parties[partyCode].Name : original.PayerName;
        List<(ReceiptLine Old, ReceiptLine New)> Copy(Guid receiptId, List<ReceiptLine> mine)
        {
            short lineNo = 0;
            var pairs = new List<(ReceiptLine Old, ReceiptLine New)>();
            foreach (var old in mine)
            {
                var copy = new ReceiptLine
                {
                    ReceiptLineId = Guid.CreateVersion7(), TenantId = original.TenantId, ReceiptId = receiptId, LineNo = ++lineNo,
                    ChargeId = old.ChargeId, ChargeCode = old.ChargeCode, Description = old.Description, ContainerNo = old.ContainerNo,
                    MovementCode = old.MovementCode, Quantity = old.Quantity, UnitRate = old.UnitRate, Amount = old.Amount,
                    TaxCode = old.TaxCode, TaxRate = old.TaxRate, TaxAmount = old.TaxAmount,
                };
                db.ReceiptLines.Add(copy);
                pairs.Add((old, copy));
            }
            return pairs;
        }
        void Pay(Guid receiptId, SplitPartRequest part)
        {
            foreach (var p in part.Payments!)
                db.ReceiptPayments.Add(new ReceiptPayment
                {
                    TenantId = original.TenantId, ReceiptId = receiptId, Channel = p.Channel.Trim().ToUpperInvariant(),
                    Amount = CashQuoter.Money(p.Amount), TenderedAmount = p.TenderedAmount is { } t ? CashQuoter.Money(t) : null,
                    ReferenceNo = p.ReferenceNo?.Trim(), BankName = p.BankName?.Trim(),
                });
        }

        // The original first: it is the row two clerks could be changing at once.
        var (first, firstLines, firstSubtotal, firstTax, firstWithheld, firstParty) = planned[0];
        var (bookingId, orderNo) = renaming ? (original.BookingId, original.OrderNo) : BookingOf(firstLines);
        var (subtotalAmount, taxAmount, withheldAmount, withheldRate) = renaming
            ? (original.SubtotalAmount, original.TaxAmount, original.WithholdingTaxAmount, original.WithholdingTaxRate)
            : (firstSubtotal, firstTax, firstWithheld, firstWithheld > 0 ? WithholdingTax.Rate : (decimal?)null);
        var payerCode = firstParty ?? original.PayerPartyCode;
        var payerName = PayerName(first, firstParty);
        var amended = await db.Receipts.Where(r => r.ReceiptId == id && r.Status == "ISSUED" && r.RowVersion == original.RowVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PayerPartyCode, payerCode).SetProperty(r => r.PayerName, payerName)
                .SetProperty(r => r.PayerTaxId, first.Payer?.TaxId?.Trim()).SetProperty(r => r.PayerBranchNo, first.Payer?.BranchNo?.Trim())
                .SetProperty(r => r.PayerAddress, first.Payer?.Address?.Trim())
                .SetProperty(r => r.BookingId, bookingId).SetProperty(r => r.OrderNo, orderNo)
                .SetProperty(r => r.SubtotalAmount, subtotalAmount).SetProperty(r => r.TaxAmount, taxAmount)
                .SetProperty(r => r.TotalAmount, subtotalAmount + taxAmount)
                .SetProperty(r => r.WithholdingTaxRate, withheldRate).SetProperty(r => r.WithholdingTaxAmount, withheldAmount)
                .SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, me), ct);
        if (amended == 0) return RevenueSupport.Conflict($"{original.ReceiptNo} was changed a moment ago.", "Reload it.");

        // Divided: the original keeps the first part's lines (numbered from 1 again) and payments.
        var kept = lines.Select(l => (Old: l, New: l)).ToList();
        if (!renaming)
        {
            await db.ReceiptLines.Where(l => l.ReceiptId == id).ExecuteDeleteAsync(ct);
            await db.ReceiptPayments.Where(p => p.ReceiptId == id).ExecuteDeleteAsync(ct);
            kept = Copy(original.ReceiptId, firstLines);
            Pay(original.ReceiptId, first);
        }
        var parted = new List<(Guid ReceiptId, List<(ReceiptLine Old, ReceiptLine New)> Lines, string? PartyCode)> { (original.ReceiptId, kept, firstParty) };

        foreach (var (part, mine, subtotal, tax, withheld, partyCode) in planned.Skip(1))
        {
            var receiptNo = await RevenueNumberSeries.NextAsync(db, RevenueNumberSeries.Receipt, original.BranchId, branch.BranchCode, branch.Local(now), ct);
            var (partBookingId, partOrderNo) = BookingOf(mine);
            var receipt = new Receipt
            {
                ReceiptId = Guid.CreateVersion7(), TenantId = original.TenantId, BranchId = original.BranchId, ReceiptNo = receiptNo,
                ReceiptAt = now, ShiftId = original.ShiftId, CashierUserId = original.CashierUserId,
                BookingId = partBookingId, OrderNo = partOrderNo,
                PayerPartyCode = partyCode ?? original.PayerPartyCode, PayerName = PayerName(part, partyCode),
                PayerTaxId = part.Payer?.TaxId?.Trim(), PayerBranchNo = part.Payer?.BranchNo?.Trim(), PayerAddress = part.Payer?.Address?.Trim(),
                Remarks = original.Remarks, CurrencyCode = original.CurrencyCode,
                SubtotalAmount = subtotal, TaxAmount = tax, TotalAmount = subtotal + tax, Status = "ISSUED",
                WithholdingTaxRate = withheld > 0 ? WithholdingTax.Rate : null, WithholdingTaxAmount = withheld,
                IssuedFrom = "GATE", SplitFromReceiptId = original.ReceiptId, CreatedBy = me,
            };
            db.Receipts.Add(receipt);
            parted.Add((receipt.ReceiptId, Copy(receipt.ReceiptId, mine), partyCode));
            Pay(receipt.ReceiptId, part);
        }
        await db.SaveChangesAsync(ct);

        // The charges follow their line to its part (status unchanged: the boxes have moved), with the part's payer.
        foreach (var (receiptId, pairs, partyCode) in parted)
            foreach (var (old, copy) in pairs)
                if (charges.GetValueOrDefault(old.ChargeId) is { } charge)
                {
                    charge.ReceiptId = receiptId;
                    charge.ReceiptLineId = copy.ReceiptLineId;
                    if (partyCode is not null) charge.PayerPartyCode = partyCode;
                    charge.UpdatedAt = now;
                }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var result = new List<ReceiptResponse>();
        foreach (var (receiptId, _, _) in parted) result.Add((await document.ReadAsync(receiptId, ct))!);
        return TypedResults.Ok<IReadOnlyList<ReceiptResponse>>(result);
    }
}

/// <param name="Remarks">Not kept: the original is no longer cancelled, so there is no cancel reason to add it to (2026-10-09).</param>
public sealed record SplitReceiptRequest(IReadOnlyList<SplitPartRequest>? Parts, string? Remarks);

/// <param name="LineNos">The original receipt's line numbers this part takes.</param>
/// <param name="PayerPartyCode">The party it is made out to (customer, haulier…); null = the original's payer.</param>
/// <param name="Payer">Tax-invoice details as chosen (name, tax id, branch, address); name null = the party's name.</param>
/// <param name="WithholdingTax">3% of the part's amount before VAT — only on a part over ฿1,000.</param>
public sealed record SplitPartRequest(
    IReadOnlyList<short>? LineNos, string? PayerPartyCode, PayerRequest? Payer, bool? WithholdingTax, IReadOnlyList<PaymentRequest>? Payments);
