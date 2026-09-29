using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Equipment;

// ── holds ───────────────────────────────────────────────────────────────────

public sealed record HoldResponse(
    Guid HoldId, string HoldCode, string DescriptionEn, string? DescriptionLocal,
    string HoldType, string BlockingScope, string ReleaseAuthority, byte Priority,
    string? DisplayColorHex, string? AutoApplyOnEvent, bool NotifyOnApply, bool IsActive, string RowVersion);

public sealed record SaveHoldRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_]{0,19}$", ErrorMessage = "Upper-case letters, digits and '_', 1-20 chars.")] string HoldCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, AllowedValues("CUSTOMS", "LEGAL", "TECHNICAL", "OPERATIONS", "FINANCE", "LINE")] string HoldType,
    [property: Required, AllowedValues("ALL", "RELEASE", "LOAD", "GATE_IN", "GATE_OUT")] string BlockingScope,
    [property: Required, AllowedValues("SUPERVISOR", "MNR", "DEPOT_OPERATIONS", "DEPOT_FINANCE", "LINE", "CUSTOMS")] string ReleaseAuthority,
    [property: Range(1, 9)] byte Priority = 5,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$")] string? DisplayColorHex = null,
    [property: MaxLength(50)] string? AutoApplyOnEvent = null,
    bool NotifyOnApply = false,
    bool IsActive = true,
    string? RowVersion = null);

// ── grades ──────────────────────────────────────────────────────────────────

public sealed record ContainerGradeResponse(
    Guid ContainerGradeId, string GradeCode, string DescriptionEn, string? DescriptionLocal,
    bool IsFoodGrade, bool IsReleasable, short RankOrder, bool IsActive, string RowVersion);

public sealed record SaveContainerGradeRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_]{0,9}$")] string GradeCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Range(1, 99)] short RankOrder,
    bool IsFoodGrade = false,
    bool IsReleasable = true,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true,
    string? RowVersion = null);

// ── conditions ──────────────────────────────────────────────────────────────

public sealed record ContainerConditionResponse(
    Guid ContainerConditionId, string ConditionCode, string DescriptionEn, string? DescriptionLocal,
    byte Severity, bool IsServiceable, bool RequiresRepair, bool CodecoDamageFlag, bool IsActive, string RowVersion);

public sealed record SaveContainerConditionRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_]{0,9}$")] string ConditionCode,
    [property: Required, MaxLength(100)] string DescriptionEn,
    [property: Range(1, 9)] byte Severity,
    bool IsServiceable = true,
    bool RequiresRepair = false,
    bool CodecoDamageFlag = false,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// The three vocabularies a yard branches on: why a box is stopped (hold), how
/// good it is (grade), and what state it is in (condition).
///
/// THESE ARE VOCABULARIES, NOT ASSIGNMENTS. MDM says a DAMAGE hold exists, blocks
/// RELEASE and is lifted by MNR; it does not say which box currently has one —
/// that is TOS's container state (ADR-007 survey seam), which is why
/// equipment.container carries no grade, condition or hold column.
///
/// They are typed tables rather than rows in config.code_list precisely because
/// the code branches on them: `is_releasable` stops a gate-out, `blocking_scope`
/// decides WHICH move a hold stops, `codeco_damage_flag` drives the CODECO DAM
/// segment. A generic code list cannot carry behaviour.
/// </summary>
internal static class EquipmentCodeEndpoints
{
    public static RouteGroupBuilder MapEquipmentCodeEndpoints(this RouteGroupBuilder master)
    {
        var holds = master.MapGroup("/holds").WithTags("Master data — holds");
        holds.MapGet("/", ListHoldsAsync).RequirePermission(MasterDataPermissions.EquipmentView).WithSummary("List hold types");
        holds.MapGet("/{holdCode}", GetHoldAsync).RequirePermission(MasterDataPermissions.EquipmentView).WithName("GetHold").WithSummary("Get one hold type");
        holds.MapPost("/", CreateHoldAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveHoldRequest>().WithSummary("Create a hold type");
        holds.MapPut("/{holdCode}", UpdateHoldAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveHoldRequest>().WithSummary("Update a hold type");
        holds.MapDelete("/{holdCode}", DeleteHoldAsync).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary("Soft-delete a hold type");

        var grades = master.MapGroup("/container-grades").WithTags("Master data — container grades");
        grades.MapGet("/", ListGradesAsync).RequirePermission(MasterDataPermissions.EquipmentView).WithSummary("List container grades");
        grades.MapPost("/", CreateGradeAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveContainerGradeRequest>().WithSummary("Create a container grade");
        grades.MapPut("/{gradeCode}", UpdateGradeAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveContainerGradeRequest>().WithSummary("Update a container grade");
        grades.MapDelete("/{gradeCode}", DeleteGradeAsync).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary("Soft-delete a container grade");

        var conditions = master.MapGroup("/container-conditions").WithTags("Master data — container conditions");
        conditions.MapGet("/", ListConditionsAsync).RequirePermission(MasterDataPermissions.EquipmentView).WithSummary("List container conditions");
        conditions.MapPost("/", CreateConditionAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveContainerConditionRequest>().WithSummary("Create a container condition");
        conditions.MapPut("/{conditionCode}", UpdateConditionAsync).RequirePermission(MasterDataPermissions.EquipmentManage).Validate<SaveContainerConditionRequest>().WithSummary("Update a container condition");
        conditions.MapDelete("/{conditionCode}", DeleteConditionAsync).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary("Soft-delete a container condition");

        return master;
    }

    // ── holds ───────────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<HoldResponse>>> ListHoldsAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var holds = db.Holds.AsNoTracking();
        if (!includeInactive) holds = holds.Where(h => h.IsActive);
        return TypedResults.Ok<IReadOnlyList<HoldResponse>>(
            await holds.OrderBy(h => h.Priority).ThenBy(h => h.HoldCode).Select(HoldProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<HoldResponse>, NotFound>> GetHoldAsync(
        string holdCode, MasterDataDbContext db, CancellationToken ct) =>
        await db.Holds.AsNoTracking().Where(h => h.HoldCode == holdCode.ToUpperInvariant())
            .Select(HoldProjection).SingleOrDefaultAsync(ct) is { } hold
            ? TypedResults.Ok(hold)
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<HoldResponse>, ValidationProblem, ProblemHttpResult>> CreateHoldAsync(
        SaveHoldRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.HoldCode.ToUpperInvariant();
        if (await ValidateHoldEventAsync(db, request.AutoApplyOnEvent, ct) is { } problem) return problem;
        if (await db.Holds.AnyAsync(h => h.HoldCode == code, ct))
            return MasterDataSupport.Conflict($"Hold '{code}' already exists.");

        var hold = new Hold { TenantId = caller.TenantId(), HoldCode = code };
        Apply(hold, request);
        db.Holds.Add(hold);
        await db.SaveChangesAsync(ct);

        return TypedResults.CreatedAtRoute(MapHold(hold), "GetHold", new { holdCode = code });
    }

    private static async Task<Results<Ok<HoldResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateHoldAsync(
        string holdCode, SaveHoldRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.HoldCode == holdCode.ToUpperInvariant(), ct);
        if (hold is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(hold, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateHoldEventAsync(db, request.AutoApplyOnEvent, ct) is { } problem) return problem;

        Apply(hold, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapHold(hold));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteHoldAsync(
        string holdCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var hold = await db.Holds.SingleOrDefaultAsync(h => h.HoldCode == holdCode.ToUpperInvariant(), ct);
        if (hold is null) return TypedResults.NotFound();
        if (db.ExpectVersion(hold, rowVersion) is { } missing) return missing;
        db.Holds.Remove(hold);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(Hold hold, SaveHoldRequest request)
    {
        hold.DescriptionEn = request.DescriptionEn;
        hold.DescriptionLocal = request.DescriptionLocal;
        hold.HoldType = request.HoldType;
        hold.BlockingScope = request.BlockingScope;
        hold.ReleaseAuthority = request.ReleaseAuthority;
        hold.Priority = request.Priority;
        hold.DisplayColorHex = request.DisplayColorHex;
        hold.AutoApplyOnEvent = request.AutoApplyOnEvent?.ToUpperInvariant();
        hold.NotifyOnApply = request.NotifyOnApply;
        hold.IsActive = request.IsActive;
    }

    /// <summary>
    /// auto_apply_on_event is a soft ref into the HOLD_EVENT code list, which is a
    /// CLOSED category (12_seed_config_definitions.sql) — the code raises those
    /// events, so a tenant inventing one produces a hold that never fires.
    /// </summary>
    private static async Task<ValidationProblem?> ValidateHoldEventAsync(
        MasterDataDbContext db, string? autoApplyOnEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(autoApplyOnEvent)) return null;

        var code = autoApplyOnEvent.ToUpperInvariant();
        // config.vw_code_list resolves the GLOBAL list plus any tenant override.
        // db.CodeListValues is the TENANT table alone, which holds no HOLD_EVENT
        // rows at all — HOLD_EVENT is a closed category owned by the platform.
        if (await db.VwCodeLists.AnyAsync(v => v.CategoryCode == "HOLD_EVENT" && v.Code == code && v.IsActive == true, ct))
            return null;

        var known = await db.VwCodeLists.AsNoTracking()
            .Where(v => v.CategoryCode == "HOLD_EVENT" && v.IsActive == true)
            .Select(v => v.Code).OrderBy(c => c).ToListAsync(ct);

        return MasterDataSupport.InvalidReference("autoApplyOnEvent",
            $"'{code}' is not a HOLD_EVENT the platform raises. Known events: {string.Join(", ", known)}.");
    }

    // ── grades ──────────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<ContainerGradeResponse>>> ListGradesAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var grades = db.ContainerGrades.AsNoTracking();
        if (!includeInactive) grades = grades.Where(g => g.IsActive);
        return TypedResults.Ok<IReadOnlyList<ContainerGradeResponse>>(
            await grades.OrderBy(g => g.RankOrder).Select(GradeProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<ContainerGradeResponse>, ValidationProblem, ProblemHttpResult>> CreateGradeAsync(
        SaveContainerGradeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.GradeCode.ToUpperInvariant();
        if (await db.ContainerGrades.AnyAsync(g => g.GradeCode == code, ct))
            return MasterDataSupport.Conflict($"Container grade '{code}' already exists.");

        var grade = new ContainerGrade { TenantId = caller.TenantId(), GradeCode = code };
        Apply(grade, request);
        db.ContainerGrades.Add(grade);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(MapGrade(grade));
    }

    private static async Task<Results<Ok<ContainerGradeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateGradeAsync(
        string gradeCode, SaveContainerGradeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var grade = await db.ContainerGrades.SingleOrDefaultAsync(g => g.GradeCode == gradeCode.ToUpperInvariant(), ct);
        if (grade is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(grade, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        Apply(grade, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapGrade(grade));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteGradeAsync(
        string gradeCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var grade = await db.ContainerGrades.SingleOrDefaultAsync(g => g.GradeCode == gradeCode.ToUpperInvariant(), ct);
        if (grade is null) return TypedResults.NotFound();
        if (db.ExpectVersion(grade, rowVersion) is { } missing) return missing;
        db.ContainerGrades.Remove(grade);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(ContainerGrade grade, SaveContainerGradeRequest request)
    {
        grade.DescriptionEn = request.DescriptionEn;
        grade.DescriptionLocal = request.DescriptionLocal;
        grade.IsFoodGrade = request.IsFoodGrade;
        grade.IsReleasable = request.IsReleasable;
        grade.RankOrder = request.RankOrder;
        grade.IsActive = request.IsActive;
    }

    // ── conditions ──────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<ContainerConditionResponse>>> ListConditionsAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var conditions = db.ContainerConditions.AsNoTracking();
        if (!includeInactive) conditions = conditions.Where(c => c.IsActive);
        return TypedResults.Ok<IReadOnlyList<ContainerConditionResponse>>(
            await conditions.OrderBy(c => c.Severity).Select(ConditionProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<ContainerConditionResponse>, ValidationProblem, ProblemHttpResult>> CreateConditionAsync(
        SaveContainerConditionRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.ConditionCode.ToUpperInvariant();
        if (await db.ContainerConditions.AnyAsync(c => c.ConditionCode == code, ct))
            return MasterDataSupport.Conflict($"Container condition '{code}' already exists.");

        var condition = new ContainerCondition { TenantId = caller.TenantId(), ConditionCode = code };
        Apply(condition, request);
        db.ContainerConditions.Add(condition);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(MapCondition(condition));
    }

    private static async Task<Results<Ok<ContainerConditionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateConditionAsync(
        string conditionCode, SaveContainerConditionRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var condition = await db.ContainerConditions.SingleOrDefaultAsync(c => c.ConditionCode == conditionCode.ToUpperInvariant(), ct);
        if (condition is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(condition, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        Apply(condition, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapCondition(condition));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteConditionAsync(
        string conditionCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var condition = await db.ContainerConditions.SingleOrDefaultAsync(c => c.ConditionCode == conditionCode.ToUpperInvariant(), ct);
        if (condition is null) return TypedResults.NotFound();
        if (db.ExpectVersion(condition, rowVersion) is { } missing) return missing;
        db.ContainerConditions.Remove(condition);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(ContainerCondition condition, SaveContainerConditionRequest request)
    {
        condition.DescriptionEn = request.DescriptionEn;
        condition.DescriptionLocal = request.DescriptionLocal;
        condition.Severity = request.Severity;
        condition.IsServiceable = request.IsServiceable;
        condition.RequiresRepair = request.RequiresRepair;
        condition.CodecoDamageFlag = request.CodecoDamageFlag;
        condition.IsActive = request.IsActive;
    }

    // ── projections ─────────────────────────────────────────────────────────

    private static readonly Expression<Func<Hold, HoldResponse>> HoldProjection = h => new HoldResponse(
        h.HoldId, h.HoldCode, h.DescriptionEn, h.DescriptionLocal, h.HoldType, h.BlockingScope,
        h.ReleaseAuthority, h.Priority, h.DisplayColorHex, h.AutoApplyOnEvent, h.NotifyOnApply,
        h.IsActive, Convert.ToBase64String(h.RowVersion));

    private static readonly Func<Hold, HoldResponse> MapHold = HoldProjection.Compile();

    private static readonly Expression<Func<ContainerGrade, ContainerGradeResponse>> GradeProjection = g => new ContainerGradeResponse(
        g.ContainerGradeId, g.GradeCode, g.DescriptionEn, g.DescriptionLocal, g.IsFoodGrade,
        g.IsReleasable, g.RankOrder, g.IsActive, Convert.ToBase64String(g.RowVersion));

    private static readonly Func<ContainerGrade, ContainerGradeResponse> MapGrade = GradeProjection.Compile();

    private static readonly Expression<Func<ContainerCondition, ContainerConditionResponse>> ConditionProjection = c => new ContainerConditionResponse(
        c.ContainerConditionId, c.ConditionCode, c.DescriptionEn, c.DescriptionLocal, c.Severity,
        c.IsServiceable, c.RequiresRepair, c.CodecoDamageFlag, c.IsActive, Convert.ToBase64String(c.RowVersion));

    private static readonly Func<ContainerCondition, ContainerConditionResponse> MapCondition = ConditionProjection.Compile();
}
