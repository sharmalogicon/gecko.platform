using Gecko.MasterData.Endpoints.Parties;

namespace Gecko.MasterData.Tests;

/// <summary>What a typed party search is turned into: company-type words dropped, one entry per word.</summary>
public sealed class PartySearchTermsTests
{
    [Theory]
    [InlineData("บ.ประสิทธิ์ เอ็นเตอร์ไพรส์ แอนด์", "ประสิทธิ์|เอ็นเตอร์ไพรส์|แอนด์")]
    [InlineData("บริษัท ประสิทธิ์ เอ็นเตอร์ไพรส์ แอนด์ เฟรต จำกัด", "ประสิทธิ์|เอ็นเตอร์ไพรส์|แอนด์|เฟรต")]
    [InlineData("บจก. เอชพีเอ็ม จำกัด (HPM)", "เอชพีเอ็ม|HPM")]
    [InlineData("หจก.ดลดี ขนส่ง", "ดลดี|ขนส่ง")]
    [InlineData("ห้างหุ้นส่วนจำกัด นาจา ทรานสปอร์ต", "นาจา|ทรานสปอร์ต")]
    [InlineData("บริษัท กิ่งดาว โลจิสติกส์ จํากัด", "กิ่งดาว|โลจิสติกส์")]
    [InlineData("PRASIT ENTERPRISE & FREIGHT CO.,LTD", "PRASIT|ENTERPRISE|FREIGHT")]
    [InlineData("R&G FUTURE LOGISTICS (THAILAND) CO., LTD.", "R&G|FUTURE|LOGISTICS|THAILAND")]
    [InlineData("  20181056 ", "20181056")]
    [InlineData("บริษัท", "บริษัท")]
    [InlineData("CO.,LTD", "CO.,LTD")]
    public void Terms(string typed, string expected) =>
        Assert.Equal(expected.Split('|'), PartySearch.Terms(typed));
}
