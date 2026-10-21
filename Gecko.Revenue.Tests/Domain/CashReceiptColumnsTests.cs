using Gecko.Revenue.Application.Reports;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>
/// Owner 2026-10-21 (KORAKIT, CashReceiptByUser_KTC_.xlsx / CashReceiptByCompany_KTC_.xlsx): the User listing prints only
/// the columns the tenant chose (CASH_RECEIPT_USER_COLUMNS), plus Other; the Company summary prints ค่าฝากตู้เกิน and ค่าไฟ when the tenant maps them.
/// </summary>
public sealed class CashReceiptColumnsTests
{
    [Fact]
    public void A_tenant_that_chose_columns_prints_only_those_and_other()
    {
        var shown = CashReceiptReports.Shown(["LIFT_ON", "FACILITIES", "GATE_1", "ELECTRICITY", "CONTAINER_STORAGE"]);

        var printed = CashReceiptReports.Columns.Where((_, i) => shown[i]).ToArray();
        Assert.Equal(["LIFT_ON", "FACILITIES", "GATE_1", "ELECTRICITY", "CONTAINER_STORAGE", "OTHER"], printed);
    }

    [Fact]
    public void A_tenant_that_chose_no_columns_prints_all_fifteen() =>
        Assert.All(CashReceiptReports.Shown([]), Assert.True);

    [Fact]
    public void The_company_summary_swaps_in_storage_and_electricity_rows_when_the_tenant_maps_them()
    {
        var company = new Dictionary<string, string> { ["A-004"] = "GATE", ["S-110"] = "CONTAINER_STORAGE", ["SE004-CA"] = "ELECTRICITY", ["S-007"] = "LIFT" };

        Assert.Equal(["ค่าผ่านท่า", "ค่าฝากตู้เกิน", "ค่าไฟ", "ค่ายกตู้", "ค่าชั่งน้ำหนัก"], CashReceiptReports.CompanyRowsFor(company).Select(r => r.Label));
        Assert.Equal(["ค่าผ่านท่า", "ค่าภาระผ่านท่า", "ค่าทำความสะอาด", "ค่ายกตู้", "ค่าชั่งน้ำหนัก"],
            CashReceiptReports.CompanyRowsFor(new Dictionary<string, string>()).Select(r => r.Label));
    }
}
