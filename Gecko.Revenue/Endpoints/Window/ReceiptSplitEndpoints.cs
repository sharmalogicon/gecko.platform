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
///   * gate receipts only (issued_from GATE), ISSUED, and only on the day they were issued;
///   * every line goes to exactly one part, two parts at least; each part is a NEW receipt
///     (CA + branch + YYMM + 5) naming the original (split_from_receipt_id);
///   * the original is cancelled "Split into …" — its coupons are NOT withdrawn and the charges
///     keep their status (the boxes have moved): they are re-pointed to the parts, with the part's payer;
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
            .WithSummary("Split a gate receipt of today into new receipts for several payers; the original is cancelled, the money is unchanged");
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
        if (original.IssuedFrom != "GATE") return RevenueSupport.Conflict($"{original.ReceiptNo} was not issued at the gate; only a gate receipt is split.");
        var branch = await calendar.BranchAsync(original.BranchId, ct);
        if (branch is null) return RevenueSupport.Conflict("This depot has no master-data profile.");
        var now = calendar.Now;
        if (branch.LocalDate(original.ReceiptAt) != branch.LocalDate(now))
            return RevenueSupport.Conflict($"{original.ReceiptNo} was issued on {branch.LocalDate(original.ReceiptAt):dd-MM-yyyy}; a receipt is split on the day it was issued only.");

        var lines = await db.ReceiptLines.AsNoTracking().Where(l => l.ReceiptId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        var paidBefore = await db.ReceiptPayments.AsNoTracking().Where(p => p.ReceiptId == id).ToListAsync(ct);

        // ── the parts ──────────────────────────────────────────────────────────
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var list)) errors[key] = list = []; list.Add(message); }
        var parts = request.Parts ?? [];
        if (parts.Count < 2) Add("parts", "Split into two parts at least.");
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
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var chargeIds = lines.Select(l => l.ChargeId).ToList();
        var charges = await db.Charges.Where(c => chargeIds.Contains(c.ChargeId)).ToDictionaryAsync(c => c.ChargeId, ct);
        var me = caller.UserId();
        var issued = new List<(Receipt Receipt, List<(ReceiptLine Old, ReceiptLine New)> Lines, string? PartyCode)>();

        foreach (var (part, mine, subtotal, tax, withheld, partyCode) in planned)
        {
            var receiptNo = await RevenueNumberSeries.NextAsync(db, RevenueNumberSeries.Receipt, original.BranchId, branch.BranchCode, branch.Local(now), ct);
            var bookings = mine.Select(l => charges.GetValueOrDefault(l.ChargeId)).OfType<Charge>().Select(c => (c.BookingId, c.OrderNo)).Distinct().ToList();
            var payerName = part.Payer?.Name?.Trim() is { Length: > 0 } named ? named
                : partyCode is not null ? parties[partyCode].Name : original.PayerName;
            var receipt = new Receipt
            {
                ReceiptId = Guid.CreateVersion7(), TenantId = original.TenantId, BranchId = original.BranchId, ReceiptNo = receiptNo,
                ReceiptAt = now, ShiftId = original.ShiftId, CashierUserId = original.CashierUserId,
                BookingId = bookings.Count == 1 ? bookings[0].BookingId : null, OrderNo = bookings.Count == 1 ? bookings[0].OrderNo : null,
                PayerPartyCode = partyCode ?? original.PayerPartyCode, PayerName = payerName,
                PayerTaxId = part.Payer?.TaxId?.Trim(), PayerBranchNo = part.Payer?.BranchNo?.Trim(), PayerAddress = part.Payer?.Address?.Trim(),
                Remarks = original.Remarks, CurrencyCode = original.CurrencyCode,
                SubtotalAmount = subtotal, TaxAmount = tax, TotalAmount = subtotal + tax, Status = "ISSUED",
                WithholdingTaxRate = withheld > 0 ? WithholdingTax.Rate : null, WithholdingTaxAmount = withheld,
                IssuedFrom = "GATE", SplitFromReceiptId = original.ReceiptId, CreatedBy = me,
            };
            db.Receipts.Add(receipt);

            short lineNo = 0;
            var pairs = new List<(ReceiptLine Old, ReceiptLine New)>();
            foreach (var old in mine)
            {
                var copy = new ReceiptLine
                {
                    ReceiptLineId = Guid.CreateVersion7(), TenantId = original.TenantId, ReceiptId = receipt.ReceiptId, LineNo = ++lineNo,
                    ChargeId = old.ChargeId, ChargeCode = old.ChargeCode, Description = old.Description, ContainerNo = old.ContainerNo,
                    MovementCode = old.MovementCode, Quantity = old.Quantity, UnitRate = old.UnitRate, Amount = old.Amount,
                    TaxCode = old.TaxCode, TaxRate = old.TaxRate, TaxAmount = old.TaxAmount,
                };
                db.ReceiptLines.Add(copy);
                pairs.Add((old, copy));
            }
            foreach (var p in part.Payments!)
                db.ReceiptPayments.Add(new ReceiptPayment
                {
                    TenantId = original.TenantId, ReceiptId = receipt.ReceiptId, Channel = p.Channel.Trim().ToUpperInvariant(),
                    Amount = CashQuoter.Money(p.Amount), TenderedAmount = p.TenderedAmount is { } t ? CashQuoter.Money(t) : null,
                    ReferenceNo = p.ReferenceNo?.Trim(), BankName = p.BankName?.Trim(),
                });
            issued.Add((receipt, pairs, partyCode));
        }
        await db.SaveChangesAsync(ct);

        // The charges follow their line to its part (status unchanged: the boxes have moved).
        foreach (var (receipt, pairs, partyCode) in issued)
            foreach (var (old, copy) in pairs)
                if (charges.GetValueOrDefault(old.ChargeId) is { } charge)
                {
                    charge.ReceiptId = receipt.ReceiptId;
                    charge.ReceiptLineId = copy.ReceiptLineId;
                    if (partyCode is not null) charge.PayerPartyCode = partyCode;
                    charge.UpdatedAt = now;
                }
        await db.SaveChangesAsync(ct);

        // The original is cancelled, naming its parts (only the void columns may change on a receipt).
        var into = string.Join(", ", issued.Select(i => i.Receipt.ReceiptNo));
        var reason = $"Split into {into}" + (string.IsNullOrWhiteSpace(request.Remarks) ? "" : $": {request.Remarks.Trim()}");
        if (reason.Length > 300) reason = reason[..300];
        var cancelled = await db.Receipts.Where(r => r.ReceiptId == id && r.Status == "ISSUED")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "VOIDED").SetProperty(r => r.VoidedAt, now)
                .SetProperty(r => r.VoidedBy, me).SetProperty(r => r.VoidReason, reason)
                .SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, me), ct);
        if (cancelled == 0) return RevenueSupport.Conflict($"{original.ReceiptNo} was changed a moment ago.", "Reload it.");
        await transaction.CommitAsync(ct);

        var result = new List<ReceiptResponse>();
        foreach (var (receipt, _, _) in issued) result.Add((await document.ReadAsync(receipt.ReceiptId, ct))!);
        return TypedResults.Ok<IReadOnlyList<ReceiptResponse>>(result);
    }
}

/// <param name="Remarks">Optional, added to the original's cancel reason ("Split into …: remarks").</param>
public sealed record SplitReceiptRequest(IReadOnlyList<SplitPartRequest>? Parts, string? Remarks);

/// <param name="LineNos">The original receipt's line numbers this part takes.</param>
/// <param name="PayerPartyCode">The party it is made out to (customer, haulier…); null = the original's payer.</param>
/// <param name="Payer">Tax-invoice details as chosen (name, tax id, branch, address); name null = the party's name.</param>
/// <param name="WithholdingTax">3% of the part's amount before VAT — only on a part over ฿1,000.</param>
public sealed record SplitPartRequest(
    IReadOnlyList<short>? LineNos, string? PayerPartyCode, PayerRequest? Payer, bool? WithholdingTax, IReadOnlyList<PaymentRequest>? Payments);
