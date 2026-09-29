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
