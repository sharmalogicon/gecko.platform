using System.Net;
using System.Net.Http.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Holds, grades and conditions — the three vocabularies the yard branches on.
/// The behaviour-carrying flags are what make these typed tables rather than
/// rows in a generic code list, so the tests assert the flags, not just the rows.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class EquipmentCodeApiTests(MasterDataApiFactory api)
{
    private const string Holds = "/api/master/holds";
    private const string Grades = "/api/master/container-grades";
    private const string Conditions = "/api/master/container-conditions";

    [Fact]
    public async Task Holds_carry_the_flags_the_gate_branches_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var holds = (await sct.GetFromJsonAsync<List<HoldRow>>(Holds, ct))!;

        var customs = holds.Single(h => h.HoldCode == "CUSTOMS");
        Assert.Equal("ALL", customs.BlockingScope);          // stops every move, not just release
        Assert.Equal("CUSTOMS", customs.ReleaseAuthority);   // and only customs lifts it

        var credit = holds.Single(h => h.HoldCode == "CREDIT");
        Assert.Equal("GATE_OUT", credit.BlockingScope);      // a box can come IN over its credit limit
        Assert.Equal("DEPOT_FINANCE", credit.ReleaseAuthority);

        // Priority orders the list, so the most serious hold is what a clerk sees first.
        Assert.True(holds.First().Priority <= holds.Last().Priority);
    }

    [Fact]
    public async Task Grades_say_whether_a_box_may_be_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var grades = (await sct.GetFromJsonAsync<List<GradeRow>>(Grades, ct))!;

        Assert.True(grades.Single(g => g.GradeCode == "A").IsFoodGrade);
        Assert.True(grades.Single(g => g.GradeCode == "A").IsReleasable);
        Assert.False(grades.Single(g => g.GradeCode == "D").IsReleasable);
        Assert.False(grades.Single(g => g.GradeCode == "SCRAP").IsReleasable);
    }

    [Fact]
    public async Task Conditions_drive_the_codeco_damage_flag()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var conditions = (await sct.GetFromJsonAsync<List<ConditionRow>>(Conditions, ct))!;

        var available = conditions.Single(c => c.ConditionCode == "AV");
        Assert.True(available.IsServiceable);
        Assert.False(available.CodecoDamageFlag);

        var damaged = conditions.Single(c => c.ConditionCode == "DM");
        Assert.False(damaged.IsServiceable);
        Assert.True(damaged.RequiresRepair);
        Assert.True(damaged.CodecoDamageFlag);   // this is what sets the CODECO DAM segment

        // Severity orders them, so "how bad is this" is answerable without a lookup table in code.
        Assert.True(available.Severity < damaged.Severity);
    }

    /// <summary>
    /// HOLD_EVENT is a CLOSED code list: the platform raises those events, so a
    /// tenant inventing one produces a hold that can never fire. The error lists
    /// the real ones rather than just saying no.
    /// </summary>
    [Fact]
    public async Task A_hold_cannot_auto_apply_on_an_event_the_platform_never_raises()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Holds, new
        {
            holdCode = NewCode("H"),
            descriptionEn = "Invented trigger",
            holdType = "OPERATIONS",
            blockingScope = "RELEASE",
            releaseAuthority = "SUPERVISOR",
            autoApplyOnEvent = "WHEN_I_FEEL_LIKE_IT",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("SURVEY_DAMAGED", body);     // the known events are listed back
        Assert.Contains("CUSTOMS_SELECTED", body);
    }

    [Fact]
    public async Task Hold_create_update_and_delete_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("H");

        var create = await sct.PostAsJsonAsync(Holds, new
        {
            holdCode = code,
            descriptionEn = "Created by test",
            holdType = "OPERATIONS",
            blockingScope = "LOAD",
            releaseAuthority = "DEPOT_OPERATIONS",
            priority = 4,
            autoApplyOnEvent = "CSC_EXPIRED",
        }, ct);
        Assert.True(create.StatusCode == HttpStatusCode.Created,
            $"POST {Holds} returned {(int)create.StatusCode}: {await create.Content.ReadAsStringAsync(ct)}");

        var created = (await create.Content.ReadFromJsonAsync<HoldRow>(ct))!;
        try
        {
            Assert.Equal("CSC_EXPIRED", created.AutoApplyOnEvent);

            var update = await sct.PutAsJsonAsync($"{Holds}/{code}", new
            {
                rowVersion = created.RowVersion,
                holdCode = code,
                descriptionEn = "Updated by test",
                holdType = "OPERATIONS",
                blockingScope = "ALL",
                releaseAuthority = "SUPERVISOR",
                priority = 1,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            var updated = (await update.Content.ReadFromJsonAsync<HoldRow>(ct))!;
            Assert.Equal("ALL", updated.BlockingScope);
            Assert.Null(updated.AutoApplyOnEvent);   // cleared by omission, as sent
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await RowVersions.DeleteCurrentAsync(sct, $"{Holds}/{code}", ct))!.StatusCode);
        }
    }

    [Fact]
    public async Task Grade_and_condition_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var gradeCode = NewCode("G");
        var conditionCode = NewCode("C");

        var grade = await sct.PostAsJsonAsync(Grades, new
        {
            gradeCode, descriptionEn = "Created by test", rankOrder = 50, isReleasable = false,
        }, ct);
        Assert.True(grade.StatusCode == HttpStatusCode.OK,
            $"POST {Grades} returned {(int)grade.StatusCode}: {await grade.Content.ReadAsStringAsync(ct)}");
        Assert.False((await grade.Content.ReadFromJsonAsync<GradeRow>(ct))!.IsReleasable);

        var condition = await sct.PostAsJsonAsync(Conditions, new
        {
            conditionCode, descriptionEn = "Created by test", severity = 7,
            isServiceable = false, requiresRepair = true, codecoDamageFlag = true,
        }, ct);
        Assert.True(condition.StatusCode == HttpStatusCode.OK,
            $"POST {Conditions} returned {(int)condition.StatusCode}: {await condition.Content.ReadAsStringAsync(ct)}");

        Assert.Equal(HttpStatusCode.NoContent, (await RowVersions.DeleteCurrentFromListAsync(sct, $"{Grades}/{gradeCode}", ct))!.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RowVersions.DeleteCurrentFromListAsync(sct, $"{Conditions}/{conditionCode}", ct))!.StatusCode);
    }

    [Fact]
    public async Task Duplicate_codes_are_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Holds, new
        {
            holdCode = "CUSTOMS",
            descriptionEn = "Duplicate",
            holdType = "CUSTOMS",
            blockingScope = "ALL",
            releaseAuthority = "CUSTOMS",
        }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task View_permission_does_not_grant_manage()
    {
        var ct = TestContext.Current.CancellationToken;
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);

        Assert.Equal(HttpStatusCode.OK, (await edi.GetAsync(Holds, ct)).StatusCode);

        var write = await edi.PostAsJsonAsync(Grades, new { gradeCode = NewCode("G"), descriptionEn = "Nope", rankOrder = 50 }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    private static object HoldBody(string code, string? rowVersion = null, string description = "Test hold") => new
    {
        holdCode = code, descriptionEn = description, holdType = "OPERATIONS",
        blockingScope = "RELEASE", releaseAuthority = "SUPERVISOR", priority = 5, rowVersion,
    };

    private static async Task<HoldRow> NewHoldAsync(HttpClient client, string code, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Holds, HoldBody(code), ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"POST {Holds} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<HoldRow>(ct))!;
    }

    [Fact]
    public async Task Hold_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("H");
        var hold = await NewHoldAsync(sct, code, ct);
        try
        {
            async Task Expect(object body, string field)
            {
                var response = await sct.PostAsJsonAsync(Holds, body, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {text}");
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {text}");
            }

            var ok = new { holdCode = NewCode("H"), descriptionEn = "X", holdType = "OPERATIONS", blockingScope = "RELEASE", releaseAuthority = "SUPERVISOR" };
            await Expect(ok with { holdCode = "bad code" }, "holdCode");
            await Expect(ok with { descriptionEn = "" }, "descriptionEn");
            await Expect(ok with { holdType = "WEATHER" }, "holdType");
            await Expect(ok with { blockingScope = "SOMETIMES" }, "blockingScope");
            await Expect(ok with { releaseAuthority = "ANYONE" }, "releaseAuthority");
            await Expect(new { ok.holdCode, ok.descriptionEn, ok.holdType, ok.blockingScope, ok.releaseAuthority, priority = 12 }, "priority");
            await Expect(new { ok.holdCode, ok.descriptionEn, ok.holdType, ok.blockingScope, ok.releaseAuthority, displayColorHex = "red" }, "displayColorHex");

            // Edits and deletes name the version they saw.
            var noVersion = await sct.PutAsJsonAsync($"{Holds}/{code}", HoldBody(code), ct);
            Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync($"{Holds}/{code}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await sct.PutAsJsonAsync($"{Holds}/{code}", HoldBody(code, hold.RowVersion, "Edited"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"{Holds}/{code}", HoldBody(code, hold.RowVersion, "Stale"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,
                (await sct.DeleteAsync(RowVersions.WithVersion($"{Holds}/{code}", hold.RowVersion), ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, $"{Holds}/{code}", ct); }
    }

    [Fact]
    public async Task Holds_need_equipment_manage_to_change_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);          // mdm.equipment.view, not manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode("H");
        var hold = await NewHoldAsync(sct, code, ct);
        try
        {
            var one = $"{Holds}/{code}";
            Assert.Equal(HttpStatusCode.OK, (await edi.GetAsync(one, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(Holds, HoldBody(NewCode("H")), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(one, HoldBody(code, hold.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.DeleteAsync(RowVersions.WithVersion(one, hold.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(one, ct)).StatusCode);
            Assert.DoesNotContain((await other.GetFromJsonAsync<List<HoldRow>>($"{Holds}?includeInactive=true", ct))!, h => h.HoldId == hold.HoldId);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(one, HoldBody(code, hold.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(one, hold.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await sct.GetAsync(one, ct)).StatusCode);   // untouched by the other tenant
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, $"{Holds}/{code}", ct); }
    }

    /// <summary>uq_hold__code ignores deleted rows: a retired code is free to be set up again.</summary>
    [Fact]
    public async Task A_deleted_hold_code_can_be_set_up_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode("H");
        try
        {
            var first = await NewHoldAsync(sct, code, ct);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{Holds}/{code}", first.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Holds}/{code}", ct)).StatusCode);

            var second = await NewHoldAsync(sct, code, ct);
            Assert.NotEqual(first.HoldId, second.HoldId);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, $"{Holds}/{code}", ct); }
    }

    private static string NewCode(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..6].ToUpperInvariant();

    private sealed record HoldRow(
        Guid HoldId, string HoldCode, string DescriptionEn, string? DescriptionLocal, string HoldType,
        string BlockingScope, string ReleaseAuthority, byte Priority, string? DisplayColorHex,
        string? AutoApplyOnEvent, bool NotifyOnApply, bool IsActive, string RowVersion);

    private sealed record GradeRow(
        Guid ContainerGradeId, string GradeCode, string DescriptionEn, string? DescriptionLocal,
        bool IsFoodGrade, bool IsReleasable, short RankOrder, bool IsActive, string RowVersion);

    private sealed record ConditionRow(
        Guid ContainerConditionId, string ConditionCode, string DescriptionEn, string? DescriptionLocal,
        byte Severity, bool IsServiceable, bool RequiresRepair, bool CodecoDamageFlag, bool IsActive, string RowVersion);
}
