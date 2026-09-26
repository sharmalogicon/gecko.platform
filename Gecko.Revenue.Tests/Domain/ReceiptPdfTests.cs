using Gecko.Revenue.Endpoints.Window;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>
/// The A4 receipt / tax invoice renders from the response alone — including the
/// cases a live test cannot reach without writing a real tax document: a VOIDED
/// receipt, and a seller whose MDM record has no tax id or address yet.
/// Invented values only; nothing here is a real company.
/// </summary>
public sealed class ReceiptPdfTests
{
    private static ReceiptResponse Receipt(string status, SellerResponse? seller) => new(
        Guid.NewGuid(), "RCT-TST01-2026-00007", new DateTimeOffset(2026, 9, 23, 14, 5, 0, TimeSpan.FromHours(7)), status, "ORD-TEST-1",
        "ลูกค้าทดสอบ จำกัด / Test Buyer Co.", "0105500000000", 1150m, 80.50m, 1230.50m, 19.50m, "THB",
        [
            new ReceiptLineResponse(1, "GATEFEE", "Gate fee", "TSTU1234565", "FULL_OUT", 1, 150m, 150m, 7m, 10.50m, "PER_CONTAINER"),
            new ReceiptLineResponse(2, "STORAGE", "Storage 18/09 - 23/09 (4 chargeable days)", "TSTU1234565", "FULL_OUT", 4, 250m, 1000m, 7m, 70m,
                "PER_DAY", new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 23)),
        ],
        [
            new ReceiptPaymentResponse("CASH", 230.50m, 250m, 19.50m, null),
            new ReceiptPaymentResponse("TRANSFER", 1000m, null, null, "SLIP-001", "Test Bank"),
        ],
        [new CouponResponse("RCT-TST01-2026-00007-1", "TSTU1234565", "FULL_OUT", null)],
        Guid.NewGuid(), "TST01", "00000", "1 Test Road, Test City 10000",
        Guid.NewGuid(), Guid.NewGuid(), seller,
        status == "VOIDED" ? new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.FromHours(7)) : null,
        status == "VOIDED" ? "Wrong payer name" : null);

    private static readonly SellerResponse FullSeller = new(
        "TST-HQ", "Test Depot Co., Ltd.", "บริษัท ทดสอบ ดีโป จำกัด", "0100000000000", "00000", true,
        "99 Test Road, Test District, Test Province 20230", "+66 0 0000 0000", null);

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("VOIDED")]
    public void A_receipt_renders_as_a_pdf(string status)
    {
        var pdf = ReceiptDocument.Render(Receipt(status, FullSeller));

        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
        Assert.True(pdf.Length > 5_000, "a real page with embedded Thai fonts is not a few hundred bytes");
    }

    [Fact]
    public void A_seller_without_a_tax_id_or_address_still_prints_and_so_does_no_seller_at_all()
    {
        var sparse = FullSeller with { TaxId = null, TaxBranchNo = null, IsHeadOffice = null, Address = null, Phone = null, NameLocal = null };

        Assert.Equal("%PDF"u8.ToArray(), ReceiptDocument.Render(Receipt("ISSUED", sparse))[..4]);
        Assert.Equal("%PDF"u8.ToArray(), ReceiptDocument.Render(Receipt("ISSUED", null))[..4]);
    }
}
