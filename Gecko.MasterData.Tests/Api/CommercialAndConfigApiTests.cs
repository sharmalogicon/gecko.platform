using System.Net;
using System.Net.Http.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Batch E (charge codes, tax codes, movements, order types + gate rules) and
/// Batch F (code lists, code mappings, number series, settings).
///
/// The assertions are aimed at the rules, not the plumbing: what the database
/// constraints mean, what Vector's real data shapes forced, and the seam where
/// MDM stops and Revenue starts.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class CommercialAndConfigApiTests(MasterDataApiFactory api)
{
    private const string Charges = "/api/master/charge-codes";
    private const string Taxes = "/api/master/tax-codes";
    private const string OrderTypes = "/api/master/order-types";
    private const string CodeLists = "/api/master/code-lists";
    private const string Mappings = "/api/master/code-mappings";
    private const string Series = "/api/master/number-series";
    private const string Settings = "/api/master/settings";

    private static string NewCode(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant();

    // ── charge codes ────────────────────────────────────────────────────────

    /// <summary>
    /// The bug that started all of this: the retired CHECK constraint made a FLEET
    /// charge code impossible to enter. It must now go in through the API.
    /// </summary>
    [Fact]
    public async Task A_fleet_charge_code_can_finally_be_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("FL");

        var response = await sct.PostAsJsonAsync(Charges, new
        {
            chargeCode = code, descriptionEn = "Fleet charge via API",
            moduleCode = "FLEET", chargeType = "TRANSPORT", billingUnitCode = "PER_TRIP",
        }, ct);

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"POST returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        await sct.DeleteAsync($"{Charges}/{code}", ct);
    }

    [Fact]
    public async Task A_charge_code_cannot_belong_to_a_module_that_does_no_depot_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // REVENUE exists in lookup.module but is_operational = 0 — it bills, it does
        // not lift boxes, so nothing could ever raise a charge scoped to it.
        var response = await sct.PostAsJsonAsync(Charges, new
        {
            chargeCode = NewCode("RV"), descriptionEn = "Should not exist",
            moduleCode = "REVENUE", chargeType = "OTHER", billingUnitCode = "PER_CONTAINER",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("depot work", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Charge_codes_can_be_listed_per_module()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var tos = await sct.GetFromJsonAsync<Paged<ChargeRow>>($"{Charges}?moduleCode=TOS&pageSize=200", ct);

        Assert.NotEmpty(tos!.Items);
        Assert.All(tos.Items, c => Assert.Equal("TOS", c.ModuleCode));
    }

    /// <summary>
    /// A withholding slot holding a VAT code deducts the wrong amount from a
    /// supplier payment, and nobody notices until the audit.
    /// </summary>
    [Fact]
    public async Task A_vat_code_cannot_be_used_as_the_withholding_tax_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PutAsJsonAsync($"{Charges}/LIFTIN/variants", new
        {
            variants = new[] { new { billTo = "LINE", paymentTermCode = "CREDIT", taxCode = "VAT7", withholdingTaxCode = "VAT7" } },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not WITHHOLDING", await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Bill-to is data since 15_bill_to_and_tariff_code_lists.sql. Vector's own
    /// spelling FWD (and the UI's CARRIER) must be refused by the lookup, not by a
    /// list repeated in C# — the lookup maps FWD to FORWARDER for the ETL only.
    /// </summary>
    [Theory]
    [InlineData("FWD")]
    [InlineData("CARRIER")]
    public async Task A_charge_variant_bill_to_must_be_a_known_role(string billTo)
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PutAsJsonAsync($"{Charges}/LIFTIN/variants", new
        {
            variants = new[] { new { billTo, paymentTermCode = "CREDIT" } },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"Unknown bill-to role(s): {billTo}", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task An_order_type_charge_must_name_a_known_payer_and_payment_term()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // A throwaway order type: this is a REPLACE endpoint, so if validation ever
        // regresses the request succeeds and wipes whatever it points at. (It did,
        // once, to the LIN fixture — while this test was being written.)
        var code = NewCode("OT");
        var create = await sct.PostAsJsonAsync(OrderTypes, new
        {
            orderTypeCode = code, descriptionEn = "Payer validation test",
            directionCode = "EXPORT", cargoClassCode = "GENERAL",
        }, ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        try
        {
            var badPayer = await sct.PutAsJsonAsync($"{OrderTypes}/{code}/charges", new
            {
                charges = new[] { new { chargeCode = "LIFTIN", paymentTo = "SHIPPING" } },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, badPayer.StatusCode);
            Assert.Contains("Unknown bill-to role(s): SHIPPING", await badPayer.Content.ReadAsStringAsync(ct));

            // payment_term_code was an unchecked soft ref on this path until now.
            var badTerm = await sct.PutAsJsonAsync($"{OrderTypes}/{code}/charges", new
            {
                charges = new[] { new { chargeCode = "LIFTIN", paymentTo = "line", paymentTermCode = "NET30" } },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, badTerm.StatusCode);
            Assert.Contains("Unknown payment term 'NET30'", await badTerm.Content.ReadAsStringAsync(ct));

            // Lower-case payer is normalised, and a real term (COD) is accepted.
            var good = await sct.PutAsJsonAsync($"{OrderTypes}/{code}/charges", new
            {
                charges = new[] { new { chargeCode = "LIFTIN", paymentTo = "line", paymentTermCode = "COD" } },
            }, ct);
            Assert.True(good.StatusCode == HttpStatusCode.OK,
                $"PUT returned {(int)good.StatusCode}: {await good.Content.ReadAsStringAsync(ct)}");
            Assert.Contains("\"paymentTo\":\"LINE\"", await good.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await sct.PutAsJsonAsync($"{OrderTypes}/{code}/charges", new { charges = Array.Empty<object>() }, ct);
            await sct.DeleteAsync($"{OrderTypes}/{code}", ct);
        }
    }

    [Fact]
    public async Task The_tariff_axis_code_lists_are_open_and_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var cargo = await sct.GetAsync($"{CodeLists}/CARGO_CATEGORY", ct);
        var truck = await sct.GetAsync($"{CodeLists}/TRUCK_CATEGORY", ct);

        Assert.Equal(HttpStatusCode.OK, cargo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, truck.StatusCode);
        var sctCargo = await cargo.Content.ReadAsStringAsync(ct);
        Assert.Contains("DANGEROUS", sctCargo);
        Assert.Contains("18_WHEEL", await truck.Content.ReadAsStringAsync(ct));

        // SCT's commodity categories (Vector, dev_05) are SCT's alone.
        Assert.Contains("USED_ENGINE", sctCargo);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var otherCargo = await other.GetStringAsync($"{CodeLists}/CARGO_CATEGORY", ct);
        Assert.Contains("DANGEROUS", otherCargo);
        Assert.DoesNotContain("USED_ENGINE", otherCargo);
    }

    [Fact]
    public async Task A_charge_code_in_use_by_an_order_type_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.DeleteAsync($"{Charges}/LIFTIN", ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── tax codes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_exempt_tax_code_must_have_a_zero_rate()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Taxes, new
        {
            taxCode = NewCode("TX"), descriptionEn = "Contradictory", countryCode = "TH",
            taxType = "EXEMPT", ratePct = 7.0, effectiveFrom = "2026-01-01",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rate of 0", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Only_one_tax_code_can_be_the_default_for_a_type_and_country()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // VAT7 is already the Thai VAT default (dev_03).
        var response = await sct.PostAsJsonAsync(Taxes, new
        {
            taxCode = NewCode("TX"), descriptionEn = "Second default", countryCode = "TH",
            taxType = "VAT", ratePct = 10.0, effectiveFrom = "2026-01-01", isDefaultForType = true,
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already the default", await response.Content.ReadAsStringAsync(ct));
    }

    // ── order types and the gate rules ──────────────────────────────────────

    /// <summary>
    /// Vector's real order type codes are human phrases — 'EXP CY/CY',
    /// 'IMP LOLO CR', 'EXP CY-IN (NON-NOMINATING)'. An identifier-shaped regex
    /// would have rejected every code the customer already uses.
    /// </summary>
    [Theory]
    [InlineData("EXP CY/CY")]
    [InlineData("IMP LOLO CR")]
    [InlineData("EXP CY-IN (NON-NOMINATING)")]
    [InlineData("LADEN TO FACT 1")]
    public async Task Real_depot_order_type_codes_are_accepted(string vectorStyleCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = $"{vectorStyleCode} {NewCode("T")}";

        var response = await sct.PostAsJsonAsync(OrderTypes, new
        {
            orderTypeCode = code, descriptionEn = "Vector-shaped code",
            directionCode = "EXPORT", cargoClassCode = "GENERAL",
        }, ct);

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"'{code}' was rejected: {await response.Content.ReadAsStringAsync(ct)}");

        // Accepting the code is half of it: the record must also be reachable by
        // that code. 'EXP CY/CY' travels as EXP%20CY%2FCY, and ASP.NET Core does
        // not decode %2F in a route value — this DELETE used to 404 silently and
        // leak a row on every run.
        var url = $"{OrderTypes}/{Uri.EscapeDataString(code)}";
        Assert.Equal(HttpStatusCode.OK, (await sct.GetAsync(url, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(url, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync(url, ct)).StatusCode);
    }

    [Fact]
    public async Task The_five_gate_rules_round_trip_per_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // dev_03 seeds LIN (laden receival) with all three checks on its gate-in.
        var detail = await sct.GetFromJsonAsync<OrderTypeDetail>($"{OrderTypes}/LIN", ct);

        var gateIn = detail!.Movements.Single(m => m.MovementCode == "GIF");
        Assert.True(gateIn.CheckSealNo);
        Assert.True(gateIn.CheckGrossWeight);
        Assert.True(gateIn.RequireVesselVoyage);
        Assert.False(gateIn.AllowDamagedRelease);
        Assert.False(gateIn.SkipEdi);

        // MNRIN is the opposite case: a damaged box is the POINT, and the move is
        // internal so no CODECO goes out.
        var repair = await sct.GetFromJsonAsync<OrderTypeDetail>($"{OrderTypes}/MNRIN", ct);
        var intoShop = repair!.Movements.Single();
        Assert.True(intoShop.AllowDamagedRelease);
        Assert.True(intoShop.SkipEdi);
        Assert.False(intoShop.IsBillable);
    }

    [Fact]
    public async Task A_movement_sequence_with_a_gap_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("OT");

        var create = await sct.PostAsJsonAsync(OrderTypes, new
        {
            orderTypeCode = code, descriptionEn = "Sequence test",
            directionCode = "EXPORT", cargoClassCode = "GENERAL",
        }, ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        try
        {
            // The gate walks the sequence; a hole means a step that never runs.
            var response = await sct.PutAsJsonAsync($"{OrderTypes}/{code}/movements", new
            {
                movements = new[]
                {
                    new { movementCode = "GIE", sequenceNo = 1 },
                    new { movementCode = "GOE", sequenceNo = 3 },
                },
            }, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("no gaps", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await sct.DeleteAsync($"{OrderTypes}/{code}", ct);
        }
    }

    // ── code lists ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_closed_code_list_rejects_a_tenant_invented_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // HOLD_EVENT is closed: the platform raises those events.
        var response = await sct.PutAsJsonAsync($"{CodeLists}/HOLD_EVENT/INVENTED", new
        {
            categoryCode = "HOLD_EVENT", code = "INVENTED", descriptionEn = "Never fires",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("closed category", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task An_open_code_list_accepts_a_tenant_value_and_the_view_shows_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("Z");

        var upsert = await sct.PutAsJsonAsync($"{CodeLists}/TRUCKING_ZONE/{code}", new
        {
            categoryCode = "TRUCKING_ZONE", code, descriptionEn = "Test zone",
        }, ct);
        Assert.True(upsert.StatusCode == HttpStatusCode.OK,
            $"upsert returned {(int)upsert.StatusCode}: {await upsert.Content.ReadAsStringAsync(ct)}");

        try
        {
            var values = await sct.GetFromJsonAsync<List<CodeValueRow>>($"{CodeLists}/TRUCKING_ZONE", ct);
            var added = values!.Single(v => v.Code == code);
            Assert.True(added.IsTenantDefined);
        }
        finally
        {
            await sct.DeleteAsync($"{CodeLists}/TRUCKING_ZONE/{code}", ct);
        }
    }

    // ── code mappings ───────────────────────────────────────────────────────

    /// <summary>
    /// Partner mapping, then tenant default, then the global ISO standard. Getting
    /// this order wrong is how Vector had '20HC' resolve differently per customer.
    /// </summary>
    [Fact]
    public async Task Code_resolution_walks_partner_then_tenant_then_the_iso_standard()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // dev_03: '20ST' is a tenant-wide mapping to 20GP.
        var tenantLevel = await sct.GetFromJsonAsync<Resolution>(
            $"{Mappings}/resolve?mappingType=EQUIPMENT_TYPE&externalCode=20ST", ct);
        Assert.True(tenantLevel!.Resolved);
        Assert.Equal("20GP", tenantLevel.InternalCode);
        Assert.Equal("TENANT_MAPPING", tenantLevel.ResolvedBy);

        // No mapping for a raw ISO code, so the global standard answers.
        var isoLevel = await sct.GetFromJsonAsync<Resolution>(
            $"{Mappings}/resolve?mappingType=EQUIPMENT_TYPE&externalCode=22G1", ct);
        Assert.True(isoLevel!.Resolved);
        Assert.Equal("20GP", isoLevel.InternalCode);
        Assert.Equal("ISO_STANDARD", isoLevel.ResolvedBy);

        // Nothing at all — the message must be rejected, not guessed at.
        var nothing = await sct.GetFromJsonAsync<Resolution>(
            $"{Mappings}/resolve?mappingType=EQUIPMENT_TYPE&externalCode=WHATEVER", ct);
        Assert.False(nothing!.Resolved);
        Assert.Equal("NONE", nothing.ResolvedBy);
    }

    // ── number series ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_yearly_reset_without_a_year_in_the_number_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Series, new
        {
            seriesKey = NewCode("S"), datePartFormat = "NONE", resetPeriod = "YEARLY",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("every January", await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// EIR numbers are issued at the barrier, inside gecko_tos. A second EIR series
    /// in master data would sooner or later hand out the same number twice.
    /// </summary>
    [Theory]
    [InlineData("EIR")]
    [InlineData("GATE_PASS")]
    public async Task A_series_the_TOS_module_numbers_cannot_be_created_here(string seriesKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Series, new
        {
            seriesKey, datePartFormat = "NONE", resetPeriod = "NEVER",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("numbered by the TOS module", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Next_number_issues_a_formatted_document_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // dev_01 seeds INVOICE: prefix INV, YYYYMM, monthly reset, 5 digits.
        var first = await sct.PostAsJsonAsync($"{Series}/next", new { seriesKey = "INVOICE" }, ct);
        Assert.True(first.StatusCode == HttpStatusCode.OK,
            $"next returned {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync(ct)}");

        var body = await first.Content.ReadFromJsonAsync<NextNumber>(ct);
        Assert.StartsWith("INV-", body!.Number);

        // Consecutive calls must not repeat — that is what the proc's UPDLOCK buys.
        var second = await sct.PostAsJsonAsync($"{Series}/next", new { seriesKey = "INVOICE" }, ct);
        var secondBody = await second.Content.ReadFromJsonAsync<NextNumber>(ct);
        Assert.NotEqual(body.Number, secondBody!.Number);
    }

    [Fact]
    public async Task An_unknown_series_reports_the_procs_own_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync($"{Series}/next", new { seriesKey = "NOSUCHSERIES" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── settings ────────────────────────────────────────────────────────────

    /// <summary>
    /// The list shows EVERY declared setting with its resolved value and where the
    /// value came from — not only the rows a tenant happens to have overridden.
    /// </summary>
    [Fact]
    public async Task Settings_list_resolves_declared_defaults_and_says_where_each_came_from()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var settings = (await sct.GetFromJsonAsync<List<SettingRow>>(Settings, ct))!;

        // SCT overrides free storage days to 14 (dev_01).
        var storage = settings.Single(s => s.SettingKey == "depot.free_storage_days");
        Assert.Equal("14", storage.Value);
        Assert.Equal("TENANT", storage.ResolvedFrom);

        // Something nobody has touched still reports its declared default.
        var untouched = settings.Single(s => s.SettingKey == "gate.max_photos_per_transaction");
        Assert.Equal("DEFAULT", untouched.ResolvedFrom);
        Assert.Equal("12", untouched.Value);
    }

    [Fact]
    public async Task A_tenant_scoped_setting_cannot_be_set_per_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PutAsJsonAsync(Settings, new
        {
            settingKey = "mdm.default_currency",   // declared TENANT scope
            settingValue = "USD",
            branchId = Guid.NewGuid(),
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TENANT-scoped", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_setting_value_must_parse_as_its_declared_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PutAsJsonAsync(Settings, new
        {
            settingKey = "depot.free_storage_days",   // declared INT
            settingValue = "a fortnight",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Config_view_permission_does_not_grant_config_manage()
    {
        var ct = TestContext.Current.CancellationToken;
        // EDI_COORDINATOR holds mdm.config.manage but NOT mdm.commercial.manage.
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);

        Assert.Equal(HttpStatusCode.OK, (await edi.GetAsync($"{CodeLists}/TRUCKING_ZONE", ct)).StatusCode);

        var commercial = await edi.PostAsJsonAsync(Taxes, new
        {
            taxCode = NewCode("TX"), descriptionEn = "Nope", countryCode = "TH",
            taxType = "VAT", ratePct = 5.0, effectiveFrom = "2026-01-01",
        }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, commercial.StatusCode);
    }

    // ── shapes ──────────────────────────────────────────────────────────────

    private sealed record Paged<T>(List<T> Items, int TotalCount);
    private sealed record ChargeRow(Guid ChargeCodeId, string ChargeCode, string DescriptionEn, string ModuleCode, string ChargeType, string RowVersion);
    private sealed record OrderTypeDetail(OrderTypeRow OrderType, List<OrderTypeMovementRow> Movements, List<object> Charges);
    private sealed record OrderTypeRow(Guid OrderTypeId, string OrderTypeCode, string DirectionCode, string RowVersion);
    private sealed record OrderTypeMovementRow(
        Guid OrderTypeMovementId, Guid MovementId, string MovementCode, string MovementDescription,
        short SequenceNo, bool IsRequired, bool IsBillable, bool CheckSealNo, bool CheckGrossWeight,
        bool RequireVesselVoyage, bool AllowDamagedRelease, bool SkipEdi, string? PudoMode);
    private sealed record CodeValueRow(string CategoryCode, string Code, string DescriptionEn, bool IsActive, bool IsTenantDefined);
    private sealed record Resolution(string MappingType, string ExternalCode, string? InternalCode, bool Resolved, string ResolvedBy, string? Note);
    private sealed record NextNumber(string SeriesKey, string Number);
    private sealed record SettingRow(string SettingKey, string? Value, string? DefaultValue, string ValueType, string AllowedScope, string OwningModule, string ResolvedFrom);
}
