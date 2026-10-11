using Gecko.MasterData.Contracts;
using Gecko.Revenue.Endpoints.Charges;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>
/// Vector's TMS.Billing.CreditInvoiceReceipt / CashInvoiceReceipt from Gecko's invoices and receipts: the SellRate grouping
/// (rate, charge, size) numbered in order, "description rate(count X size')", and amounts that are the lines' own.
/// Invented values only.
/// </summary>
public sealed class InvoiceFormPdfTests
{
    private static readonly InvoiceFormLine[] Lines =
    [
        new("SL004-CR", "LIFT ON CHARGE", 1200m, "20", 1200m, 84m),
        new("SE004-CR", "STORAGE", 46.73m, "40", 467.30m, 32.71m),   // 10 days: the amount is the line's, not rate × count
        new("SL004-CR", "LIFT ON CHARGE", 1200m, "20", 1200m, 84m),
        new("SL004-CR", "LIFT ON CHARGE", 1800m, "40", 1800m, 126m),
    ];

    [Fact]
    public void Lines_group_by_rate_charge_and_size_in_first_appearance_order()
    {
        var rows = InvoiceFormDocument.Rows(Lines);

        Assert.Equal(
        [
            new InvoiceFormRow(1, "LIFT ON CHARGE 1200.00(2 X 20')", 2400m, 168m),
            new InvoiceFormRow(2, "STORAGE 46.73(1 X 40')", 467.30m, 32.71m),
            new InvoiceFormRow(3, "LIFT ON CHARGE 1800.00(1 X 40')", 1800m, 126m),
        ], rows);
    }

    private static InvoiceForm Form(InvoiceFormPayment? payment, InvoicingCompanyRef? seller, bool voided = false) => new(
        "CR-TST01-2610-00001", new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(7)), "Test Buyer Co., Ltd.",
        "1 Test Road", "Test District Bangkok 10000", "+66 0 0000 0000", "EXPORT", "TEST VESSEL", "001E",
        new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(7)), "SUB-1", "ORD-TEST-1",
        4667.30m, 326.71m, 4994.01m, payment, voided, InvoiceFormDocument.Rows(Lines), seller);

    private static readonly InvoicingCompanyRef Seller = new(Guid.NewGuid(), "TST", "Test Depot Co., Ltd.", null, "0100000000000", "00000", true,
        "99 Test Road", null, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_credit_form_renders_with_or_without_a_seller(bool withSeller)
    {
        var pdf = InvoiceFormDocument.RenderCredit(Form(null, withSeller ? Seller : null));
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
    }

    [Fact]
    public void The_cash_form_renders_for_a_receipt_a_credit_invoice_and_a_void()
    {
        Assert.Equal("%PDF"u8.ToArray(), InvoiceFormDocument.RenderCash(Form(new InvoiceFormPayment("CHEQUE + CASH", "CHQ-1", 4994.01m), Seller))[..4]);
        Assert.Equal("%PDF"u8.ToArray(), InvoiceFormDocument.RenderCash(Form(null, Seller))[..4]);
        Assert.Equal("%PDF"u8.ToArray(), InvoiceFormDocument.RenderCash(Form(null, null, voided: true))[..4]);
    }

    [Fact]
    public void A_long_invoice_runs_onto_more_pages()
    {
        var many = Enumerable.Range(1, 60).Select(i => new InvoiceFormLine($"C{i:00}", $"Charge {i}", i, "20", i, 0m)).ToList();
        var form = Form(null, Seller) with { Rows = InvoiceFormDocument.Rows(many) };
        Assert.Equal("%PDF"u8.ToArray(), InvoiceFormDocument.RenderCredit(form)[..4]);
        Assert.Equal("%PDF"u8.ToArray(), InvoiceFormDocument.RenderCash(form)[..4]);
    }
}
