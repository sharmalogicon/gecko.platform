using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Revenue.Endpoints.Window;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// POST /api/revenue/window/receipts/{id}/split through the real host (owner 2026-10-09):
/// a change of payer keeps the receipt and its number; a split keeps the number on its first part.
///
/// A drawer and its receipts are written straight into cashier.* as the fixture tenant (SCT LCB01),
/// numbered ZZS-, and removed in finally — with any part the split numbered.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ReceiptSplitApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string NewPayer = "CUS-BKF";      // Bangkok Freight Forwarders Co., Ltd.
    private const string OtherPayer = "AGT-SEA";

    private static string Split(Guid id) => $"/api/revenue/window/receipts/{id}/split";

    private async Task<Guid> UserIdAsync(string email, CancellationToken ct)
    {
        var client = await api.ClientForAsync(email);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        return me.RootElement.GetProperty("userId").GetGuid();
    }

    /// <summary>An open drawer and one issued receipt on it: a line per amount (VAT 7%), paid in cash.</summary>
    private static async Task<(Guid Shift, Receipt Receipt)> SeedAsync(Guid cashier, string issuedFrom, DateTimeOffset at, decimal[] amounts, CancellationToken ct)
    {
        var shift = new Shift
        {
            ShiftId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
            OpenedAt = at.AddHours(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = at.AddHours(1), ClosedBy = cashier,
            CreatedAt = at, UpdatedAt = at,
        };
        var subtotal = amounts.Sum();
        var tax = amounts.Sum(a => Math.Round(a * 0.07m, 2));
        var receipt = new Receipt
        {
            ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = $"ZZS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            ReceiptAt = at, ShiftId = shift.ShiftId, CashierUserId = cashier, OrderNo = "ZZS-ORDER",
            PayerPartyCode = "CUS-TAE", PayerName = "Customer A", PayerTaxId = "0000000000001", PayerAddress = "Old address",
            CurrencyCode = "THB", SubtotalAmount = subtotal, TaxAmount = tax, TotalAmount = subtotal + tax,
            Status = "ISSUED", IssuedFrom = issuedFrom, CreatedAt = at, UpdatedAt = at,
        };
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        db.Shifts.Add(shift);
        db.Receipts.Add(receipt);
        short n = 0;
        foreach (var amount in amounts)
            db.ReceiptLines.Add(new ReceiptLine
            {
                ReceiptLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receipt.ReceiptId, LineNo = ++n,
                ChargeId = Guid.NewGuid(), ChargeCode = $"ZZ-{n}", Description = $"Charge {n}", ContainerNo = $"ZZSU100000{n}",
                MovementCode = "MTY_OUT", Quantity = 1, UnitRate = amount, Amount = amount,
                TaxCode = "VAT7", TaxRate = 7m, TaxAmount = Math.Round(amount * 0.07m, 2), CreatedAt = at,
            });
        db.ReceiptPayments.Add(new ReceiptPayment
        {
            ReceiptPaymentId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receipt.ReceiptId, Channel = "CASH",
            Amount = subtotal + tax, CreatedAt = at,
        });
        await db.SaveChangesAsync(ct);
        return (shift.ShiftId, receipt);
    }

    /// <summary>The shift's receipts (parts before the receipt they were split from), their lines and payments, then the shift.</summary>
    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE l FROM cashier.receipt_line l JOIN cashier.receipt r ON r.receipt_id = l.receipt_id WHERE r.shift_id = @shift;
            DELETE p FROM cashier.receipt_payment p JOIN cashier.receipt r ON r.receipt_id = p.receipt_id WHERE r.shift_id = @shift;
            DELETE FROM cashier.receipt WHERE shift_id = @shift AND split_from_receipt_id IS NOT NULL;
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    private static object Part(short[] lineNos, string payer, decimal cash, string? taxId = null, string? address = null) => new
    {
        lineNos, payerPartyCode = payer, payer = new { name = (string?)null, taxId, branchNo = "00000", address },
        withholdingTax = false, payments = new[] { new { channel = "CASH", amount = cash } },
    };

    [Fact]
    public async Task Changing_the_payer_keeps_the_receipt_and_its_number_on_any_receipt_any_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var cashier = await UserIdAsync(RevenueApiFactory.SctAccounts, ct);
        // A WINDOW receipt from 2020: neither a gate receipt nor today's — a change of payer takes it anyway.
        var (shift, original) = await SeedAsync(cashier, "WINDOW", new DateTimeOffset(2020, 1, 15, 12, 0, 0, TimeSpan.FromHours(7)), [1000m], ct);
        try
        {
            var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
            var response = await client.PostAsJsonAsync(Split(original.ReceiptId),
                new { parts = new[] { Part([1], NewPayer, 1070m, taxId: "0105551234567", address: "1 New Road, Bangkok") } }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);

            var receipt = Assert.Single(JsonSerializer.Deserialize<List<ReceiptResponse>>(body, JsonSerializerOptions.Web)!);
            Assert.Equal((original.ReceiptId, original.ReceiptNo, "ISSUED"), (receipt.ReceiptId, receipt.ReceiptNo, receipt.Status));
            Assert.Equal((NewPayer, "Bangkok Freight Forwarders Co., Ltd.", "0105551234567", "1 New Road, Bangkok"),
                (receipt.PayerPartyCode, receipt.PayerName, receipt.PayerTaxId, receipt.PayerAddress));
            Assert.Equal((1000m, 70m, 1070m), (receipt.Subtotal, receipt.Tax, receipt.Total));

            // Nothing new was issued and nothing voided: the drawer holds the one receipt, its line and its payment.
            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            var onShift = await db.Receipts.AsNoTracking().Where(r => r.ShiftId == shift).ToListAsync(ct);
            var only = Assert.Single(onShift);
            Assert.Null(only.VoidedAt);
            Assert.Equal(original.ReceiptAt, only.ReceiptAt);
            Assert.Single(await db.ReceiptLines.AsNoTracking().Where(l => l.ReceiptId == original.ReceiptId).ToListAsync(ct));
            Assert.Equal(1070m, Assert.Single(await db.ReceiptPayments.AsNoTracking().Where(p => p.ReceiptId == original.ReceiptId).ToListAsync(ct)).Amount);
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task A_split_keeps_the_original_number_on_its_first_part()
    {
        var ct = TestContext.Current.CancellationToken;
        var cashier = await UserIdAsync(RevenueApiFactory.SctAccounts, ct);
        var (shift, original) = await SeedAsync(cashier, "GATE", DateTimeOffset.UtcNow, [1000m, 500m], ct);
        try
        {
            var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
            var response = await client.PostAsJsonAsync(Split(original.ReceiptId),
                new { parts = new[] { Part([2], NewPayer, 535m), Part([1], OtherPayer, 1070m) } }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);

            var parts = JsonSerializer.Deserialize<List<ReceiptResponse>>(body, JsonSerializerOptions.Web)!;
            Assert.Equal(2, parts.Count);
            var (kept, other) = (parts[0], parts[1]);

            // The first part is the original receipt, cut down to its line (numbered 1 again) and its money.
            Assert.Equal((original.ReceiptId, original.ReceiptNo, "ISSUED", NewPayer), (kept.ReceiptId, kept.ReceiptNo, kept.Status, kept.PayerPartyCode));
            Assert.Equal((500m, 35m, 535m), (kept.Subtotal, kept.Tax, kept.Total));
            var keptLine = Assert.Single(kept.Lines);
            Assert.Equal((1, 500m), ((int)keptLine.LineNo, keptLine.Amount));
            Assert.Equal(535m, Assert.Single(kept.Payments).Amount);

            // The other part is a new receipt naming it.
            Assert.NotEqual(original.ReceiptNo, other.ReceiptNo);
            Assert.Equal((OtherPayer, 1070m, original.ReceiptNo), (other.PayerPartyCode, other.Total, other.SplitFromReceiptNo));
            Assert.Equal(1000m, Assert.Single(other.Lines).Amount);

            // The drawer took the same money: ฿535 + ฿1,070 = the ฿1,605 the original took.
            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            var ids = parts.Select(p => p.ReceiptId).ToList();
            Assert.Equal(1605m, await db.ReceiptPayments.AsNoTracking().Where(p => ids.Contains(p.ReceiptId)).SumAsync(p => p.Amount, ct));
            Assert.Equal(2, await db.Receipts.AsNoTracking().CountAsync(r => r.ShiftId == shift && r.Status == "ISSUED", ct));
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task Dividing_is_still_for_a_gate_receipt_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var cashier = await UserIdAsync(RevenueApiFactory.SctAccounts, ct);
        var (shift, original) = await SeedAsync(cashier, "WINDOW", DateTimeOffset.UtcNow, [1000m, 500m], ct);
        try
        {
            var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
            var response = await client.PostAsJsonAsync(Split(original.ReceiptId),
                new { parts = new[] { Part([1], NewPayer, 1070m), Part([2], OtherPayer, 535m) } }, ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { await RemoveAsync(shift); }
    }
}
