using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The customer register through the real host: search, detail, registering at
/// the counter (a branch-scoped GATE_CLERK), the tax-id duplicate rule, edits with
/// optimistic concurrency, and tenant isolation. Fixture tenants SCT and
/// SIAM-COMMERCIAL only — never KORAKIT, whose parties are real customers.
/// Every party a test registers is soft-deleted in a finally, and the factory
/// purges soft-deleted API rows at the end of the run.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class PartyApiTests(MasterDataApiFactory api)
{
    private const string Base = "/api/master/parties";

    /// <summary>GATE_CLERK at SCT-LCB01 only — every grant arrives through `bpm`.</summary>
    private const string SctGateClerk = "gate1.lcb@sct.co.th";

    /// <summary>ACCOUNTS, tenant-wide — edits parties since 19_party_permissions.sql.</summary>
    private const string SctAccounts = "accounts@sct.co.th";

    // dev_02 fixture parties in SCT.
    private const string ThaiAgro = "CUS-TAE";            // tax 0105539900112, alias DEBTOR D-TAE
    private const string ThaiAgroTaxId = "0105539900112";

    [Theory]
    [InlineData("CUS-TAE")]                   // party code
    [InlineData("thai agro")]                 // English name, lower case
    [InlineData("ไทย อะโกร")]                 // Thai name
    [InlineData(ThaiAgroTaxId)]               // tax id
    [InlineData("d-tae")]                     // alias (debtor code), lower case
    public async Task Search_matches_code_names_tax_id_and_alias(string search)
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var page = await sct.GetFromJsonAsync<Paged<Summary>>($"{Base}?search={Uri.EscapeDataString(search)}", ct);

        var hit = Assert.Single(page!.Items, p => p.PartyCode == ThaiAgro);
        Assert.Equal(ThaiAgroTaxId, hit.TaxId);
        Assert.Contains("CUSTOMER", hit.Roles);
    }

    [Fact]
    public async Task Role_filter_uses_the_extension_tables_and_the_list_is_ordered_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var hauliers = await sct.GetFromJsonAsync<Paged<Summary>>($"{Base}?role=HAULIER&pageSize=200", ct);
        Assert.Contains(hauliers!.Items, p => p.PartyCode == "HAU-SHT");
        Assert.All(hauliers.Items, p => Assert.Contains("HAULIER", p.Roles));
        Assert.DoesNotContain(hauliers.Items, p => p.PartyCode == ThaiAgro);

        var lines = await sct.GetFromJsonAsync<Paged<Summary>>($"{Base}?role=SHIPPING_LINE&pageSize=200", ct);
        Assert.Contains(lines!.Items, p => p.PartyCode == "MAEU");
        var names = lines.Items.Select(p => p.NameEn).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names, StringComparer.OrdinalIgnoreCase);

        var bad = await sct.GetAsync($"{Base}?role=PIRATE", ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Paging_is_honoured()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var page = await sct.GetFromJsonAsync<Paged<Summary>>($"{Base}?page=2&pageSize=5", ct);

        Assert.Equal(2, page!.Page);
        Assert.Equal(5, page.PageSize);
        Assert.True(page.TotalCount >= 18);
        Assert.Equal(5, page.Items.Count);
    }

    [Fact]
    public async Task Detail_carries_address_aliases_contacts_and_a_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var detail = await sct.GetFromJsonAsync<Detail>($"{Base}/{ThaiAgro}", ct);

        Assert.Equal(ThaiAgro, detail!.PartyCode);
        Assert.Equal("บริษัท ไทย อะโกร เอ็กซ์ปอร์ต จำกัด", detail.NameLocal);
        Assert.Equal("45 Rama IV Road", detail.Address);
        Assert.Equal("TH", detail.CountryCode);
        Assert.Contains(detail.Aliases, a => a.Code == "D-TAE" && a.Type == "DEBTOR");
        Assert.Contains(detail.Contacts, c => c.Role == "SHIPPING" && c.Email == "export@thaiagro.co.th");
        Assert.False(string.IsNullOrEmpty(detail.RowVersion));
    }

    [Fact]
    public async Task Unknown_code_is_404_including_a_slash_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Base}/NO-SUCH-PARTY", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Base}/{Uri.EscapeDataString("NO/SUCH")}", ct)).StatusCode);
    }

    [Fact]
    public async Task A_gate_clerk_can_search_and_register_a_customer_but_not_edit_or_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(SctGateClerk);
        var admin = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var taxId = NewTaxId();
        string? code = null;
        try
        {
            var search = await clerk.GetAsync($"{Base}?search=agro", ct);
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);

            // No roles sent: a counter registration is a customer.
            var created = await clerk.PostAsJsonAsync(Base, new
            {
                nameEn = "Test Walk-in Trading Co., Ltd.",
                nameLocal = "บริษัท ทดสอบ วอล์คอิน เทรดดิ้ง จำกัด",
                taxId,
                branchNo = "00000",
                address = "1 Test Road, Si Racha",
                phone = "+66-38-000-000",
                email = "walkin@example.co.th",
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var party = (await created.Content.ReadFromJsonAsync<Detail>(ct))!;
            code = party.PartyCode;

            Assert.StartsWith("P", code);                      // PARTY_CODE series: 'P' + 6 digits
            Assert.Equal(7, code.Length);
            Assert.Equal(["CUSTOMER"], party.Roles);
            Assert.Equal("บริษัท ทดสอบ วอล์คอิน เทรดดิ้ง จำกัด", party.NameLocal);
            Assert.Equal(taxId, party.TaxId);
            Assert.Equal("00000", party.BranchNo);
            Assert.EndsWith($"{Base}/{code}", created.Headers.Location!.ToString());

            var found = await clerk.GetFromJsonAsync<Paged<Summary>>($"{Base}?search={taxId}", ct);
            Assert.Equal(code, Assert.Single(found!.Items).PartyCode);

            var edit = await clerk.PutAsJsonAsync($"{Base}/{code}", new { nameEn = "Renamed", rowVersion = party.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clerk.DeleteAsync($"{Base}/{code}", ct)).StatusCode);
        }
        finally
        {
            if (code is not null) await DeleteAsync(admin, code, ct);
        }
    }

    [Fact]
    public async Task Same_tax_id_and_branch_is_409_with_the_existing_code_and_a_bad_tax_id_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var duplicate = await admin.PostAsJsonAsync(Base, new { nameEn = "Thai Agro again", taxId = ThaiAgroTaxId }, ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync(ct));
        Assert.Equal(ThaiAgro, problem.RootElement.GetProperty("existingPartyCode").GetString());

        foreach (var bad in new[] { "12345", "01055399001120", "010553990011X" })
        {
            var response = await admin.PostAsJsonAsync(Base, new { nameEn = "Bad tax id", taxId = bad }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var unknownRole = await admin.PostAsJsonAsync(Base, new { nameEn = "Bad role", roles = new[] { "PIRATE" } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
    }

    [Fact]
    public async Task Accounts_can_edit_and_a_stale_row_version_is_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var accounts = await api.ClientForAsync(SctAccounts);
        string? code = null;
        try
        {
            var created = await admin.PostAsJsonAsync(Base, new
            {
                nameEn = "Test Edit Logistics Co., Ltd.",
                taxId = NewTaxId(),
                branchNo = "00001",
                roles = new[] { "CUSTOMER", "HAULIER" },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var party = (await created.Content.ReadFromJsonAsync<Detail>(ct))!;
            code = party.PartyCode;
            Assert.Equal(["CUSTOMER", "HAULIER"], party.Roles);

            var updated = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Test Edit Logistics Co., Ltd.",
                nameLocal = "บริษัท ทดสอบ แก้ไข โลจิสติกส์ จำกัด",
                taxId = party.TaxId,
                branchNo = "00001",
                phone = "+66-2-000-0001",
                roles = new[] { "CUSTOMER", "FORWARDER" },
                rowVersion = party.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var after = (await updated.Content.ReadFromJsonAsync<Detail>(ct))!;
            Assert.Equal("+66-2-000-0001", after.Phone);
            Assert.Equal("บริษัท ทดสอบ แก้ไข โลจิสติกส์ จำกัด", after.NameLocal);
            Assert.Equal(["CUSTOMER", "FORWARDER"], after.Roles);
            Assert.NotEqual(party.RowVersion, after.RowVersion);

            // The first rowVersion is now stale.
            var stale = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Lost update", taxId = party.TaxId, branchNo = "00001", rowVersion = party.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            var missing = await accounts.PutAsJsonAsync($"{Base}/{code}", new { nameEn = "No version" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

            // Re-adding a removed role restores its row rather than colliding on the key.
            var restored = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Test Edit Logistics Co., Ltd.", taxId = party.TaxId, branchNo = "00001",
                roles = new[] { "CUSTOMER", "HAULIER" }, rowVersion = after.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Equal(["CUSTOMER", "HAULIER"], (await restored.Content.ReadFromJsonAsync<Detail>(ct))!.Roles);
        }
        finally
        {
            if (code is not null) await DeleteAsync(admin, code, ct);
        }
    }

    [Fact]
    public async Task Remarks_website_and_registration_no_round_trip_and_a_new_party_gets_the_company_currency()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var accounts = await api.ClientForAsync(SctAccounts);
        string? code = null;
        try
        {
            // No currency sent: SCT's company (org.company) invoices in THB.
            var created = await admin.PostAsJsonAsync(Base, new
            {
                nameEn = "Test Remarks Trading Co., Ltd.",
                taxId = NewTaxId(),
                website = "https://remarks.example.co.th",
                remarks = "Pays cash at the counter; ask for the stamp.",
                registrationNo = "0105560000001",
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var party = (await created.Content.ReadFromJsonAsync<Detail>(ct))!;
            code = party.PartyCode;
            Assert.Equal("THB", party.DefaultCurrency);
            Assert.Equal("https://remarks.example.co.th", party.Website);
            Assert.Equal("Pays cash at the counter; ask for the stamp.", party.Remarks);
            Assert.Equal("0105560000001", party.RegistrationNo);

            // A PUT that does not mention them leaves them alone.
            var keep = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Test Remarks Trading Co., Ltd.", taxId = party.TaxId, phone = "+66-2-000-0002", rowVersion = party.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
            var kept = (await keep.Content.ReadFromJsonAsync<Detail>(ct))!;
            Assert.Equal("Pays cash at the counter; ask for the stamp.", kept.Remarks);
            Assert.Equal("https://remarks.example.co.th", kept.Website);
            Assert.Equal("THB", kept.DefaultCurrency);

            // An unknown currency is refused; a real one is taken; "" clears remarks.
            var bad = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Test Remarks Trading Co., Ltd.", taxId = party.TaxId, defaultCurrency = "XQZ", rowVersion = kept.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var changed = await accounts.PutAsJsonAsync($"{Base}/{code}", new
            {
                nameEn = "Test Remarks Trading Co., Ltd.", taxId = party.TaxId, defaultCurrency = "usd", remarks = "",
                rowVersion = kept.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var after = (await changed.Content.ReadFromJsonAsync<Detail>(ct))!;
            Assert.Equal("USD", after.DefaultCurrency);
            Assert.Null(after.Remarks);
            Assert.Equal("0105560000001", after.RegistrationNo);

            var tooLong = await admin.PostAsJsonAsync(Base, new { nameEn = "Too long", remarks = new string('x', 1001) }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        }
        finally
        {
            if (code is not null) await DeleteAsync(admin, code, ct);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_touch_the_party()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var taxId = NewTaxId();
        string? code = null;
        try
        {
            var created = await sct.PostAsJsonAsync(Base, new { nameEn = "Test Isolation Co., Ltd.", taxId }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var party = (await created.Content.ReadFromJsonAsync<Detail>(ct))!;
            code = party.PartyCode;

            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Base}/{code}", ct)).StatusCode);
            Assert.Empty((await other.GetFromJsonAsync<Paged<Summary>>($"{Base}?search={taxId}", ct))!.Items);
            Assert.Empty((await other.GetFromJsonAsync<Paged<Summary>>($"{Base}?search={ThaiAgroTaxId}", ct))!.Items);
            var put = await other.PutAsJsonAsync($"{Base}/{code}", new { nameEn = "Hijack", rowVersion = party.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

            // The same tax id is free in another tenant: duplicates are per tenant.
            var otherCreate = await other.PostAsJsonAsync(Base, new { nameEn = "Test Isolation Co., Ltd.", taxId }, ct);
            Assert.Equal(HttpStatusCode.Created, otherCreate.StatusCode);
            var otherCode = (await otherCreate.Content.ReadFromJsonAsync<Detail>(ct))!.PartyCode;
            await DeleteAsync(other, otherCode, ct);
        }
        finally
        {
            if (code is not null) await DeleteAsync(sct, code, ct);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task DeleteAsync(HttpClient client, string code, CancellationToken ct)
    {
        var response = await client.DeleteAsync($"{Base}/{Uri.EscapeDataString(code)}", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>A 13-digit tax id no fixture uses ('9' prefix), unique per call.</summary>
    private static string NewTaxId() => "9" + Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12");

    private sealed record Paged<T>(List<T> Items, int Page, int PageSize, int TotalCount);

    private sealed record Summary(Guid PartyId, string PartyCode, string NameEn, string? NameLocal, string? TaxId, string? BranchNo, bool IsActive, List<string> Roles);

    private sealed record Alias(string Code, string Type, string? Label);

    private sealed record Contact(string? Name, string Role, string? Phone, string? Email);

    private sealed record Detail(
        Guid PartyId, string PartyCode, string NameEn, string? NameLocal, string? TaxId, string? BranchNo,
        bool IsActive, List<string> Roles, string CountryCode, string? Address, string? Phone, string? Email,
        List<Alias> Aliases, List<Contact> Contacts, string RowVersion,
        string? DefaultCurrency = null, string? Website = null, string? Remarks = null, string? RegistrationNo = null);
}
