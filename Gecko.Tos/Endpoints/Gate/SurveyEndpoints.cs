using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

// ── contracts ───────────────────────────────────────────────────────────────

public sealed record SurveyResponse(
    Guid SurveyId, Guid ContainerVisitId, Guid? GateTransactionId, string ContainerNo, Guid BranchId,
    string SurveyType, DateTimeOffset SurveyedAt, Guid? SurveyedBy, string? SurveyorName,
    string? ConditionCode, string? GradeCode, bool IsServiceable, string? Remarks,
    IReadOnlyList<SurveyDamageResponse> Damages,
    IReadOnlyList<string> HoldsApplied, string RowVersion);

public sealed record SurveyDamageResponse(
    Guid SurveyDamageId, short LineNo, string? LocationCode, string? ComponentCode,
    string DamageCode, string? DamageDescription, bool MakesUnserviceable,
    decimal? LengthCm, decimal? WidthCm, short Quantity, bool IsPreExisting, string? Remarks);

public sealed record SurveyDamageRequest(
    [property: Required, MaxLength(20)] string DamageCode,
    [property: MaxLength(20)] string? LocationCode = null,
    [property: MaxLength(20)] string? ComponentCode = null,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    short Quantity = 1,
    bool IsPreExisting = false,
    [property: MaxLength(500)] string? Remarks = null);

public sealed record SaveSurveyRequest(
    [property: Required, MaxLength(11)] string ContainerNo,
    [property: Required, AllowedValues("GATE_IN", "GATE_OUT", "PTI", "RE_SURVEY", "PERIODIC")] string SurveyType,
    Guid? GateTransactionId = null,
    DateTimeOffset? SurveyedAt = null,
    [property: MaxLength(100)] string? SurveyorName = null,
    [property: MaxLength(20)] string? ConditionCode = null,
    [property: MaxLength(20)] string? GradeCode = null,
    [property: MaxLength(1000)] string? Remarks = null,
    IReadOnlyList<SurveyDamageRequest>? Damages = null);

/// <summary>
/// Surveys at the gate (PLAN §4.4, V-16).
///
/// Vector's ContainerMovementDamageStatus has **one row in 2.7 years**: there is no
/// survey process in it at all, which is why every damage argument at a Thai depot
/// is settled with a phone photo and a raised voice.
///
/// Here a survey is a record with a surveyor, a condition, a grade and typed damages
/// in CEDEX terms — the seam MnR will estimate from (ADR-007). Two consequences the
/// code enforces: a damage code that MAKES THE BOX UNSERVICEABLE decides that, not
/// the surveyor's tick box; and an unserviceable box picks up the hold its depot
/// configured for exactly that event, automatically, with the survey on the record.
/// </summary>
internal static class SurveyEndpoints
{
    /// <summary>MDM <c>equipment.hold.auto_apply_on_event</c>.</summary>
    private const string DamagedEvent = "SURVEY_DAMAGED";

    public static RouteGroupBuilder MapSurveyEndpoints(this RouteGroupBuilder tos)
    {
        var surveys = tos.MapGroup("/gate/surveys").WithTags("TOS — surveys");

        surveys.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Surveys, newest first");
        surveys.MapGet("/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithName("GetSurvey").WithSummary("One survey with its damages");
        surveys.MapPost("/", CreateAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<SaveSurveyRequest>()
            .WithSummary("Record a survey on a box that is in the yard")
            .WithDescription("Damages are CEDEX (location / component / damage). A damage code flagged MakesUnserviceable forces isServiceable = false and applies any hold the depot set to auto-apply on SURVEY_DAMAGED.");

        return tos;
    }

    private static async Task<Results<Ok<PagedResult<SurveyResponse>>, ValidationProblem>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        CancellationToken ct, string? containerNo = null, Guid? branchId = null, bool? damagedOnly = null)
    {
        var rows = db.Surveys.AsNoTracking();

        if (ContainerNumber.Normalise(containerNo ?? "") is { Length: > 0 } box) rows = rows.Where(s => s.ContainerNo == box);
        if (branchId is not null) rows = rows.Where(s => s.BranchId == branchId);
        if (damagedOnly == true) rows = rows.Where(s => !s.IsServiceable);
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(s => allowed.Contains(s.BranchId));
        }

        var page = await rows.OrderByDescending(s => s.SurveyedAt).ToPagedAsync(query.Page, query.PageSize, ct);
        var ids = page.Items.Select(s => s.SurveyId).ToList();
        var damages = await db.SurveyDamages.AsNoTracking().Where(d => ids.Contains(d.SurveyId)).ToListAsync(ct);
        var codes = await master.SurveyCodesAsync(ct);

        return TypedResults.Ok(new PagedResult<SurveyResponse>(
            page.Items.Select(s => Project(s, damages.Where(d => d.SurveyId == s.SurveyId), codes, [])).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<SurveyResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, IMasterDataReferences master, ICallerPermissions scope, CancellationToken ct)
    {
        var survey = await db.Surveys.AsNoTracking().SingleOrDefaultAsync(s => s.SurveyId == id, ct);
        if (survey is null || !scope.HasAt(TosPermissions.GateView, survey.BranchId)) return TypedResults.NotFound();

        var damages = await db.SurveyDamages.AsNoTracking().Where(d => d.SurveyId == id).ToListAsync(ct);
        return TypedResults.Ok(Project(survey, damages, await master.SurveyCodesAsync(ct), []));
    }

    private static async Task<Results<Created<SurveyResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveSurveyRequest request, TosDbContext db, IMasterDataReferences master,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var containerNo = ContainerNumber.Normalise(request.ContainerNo);
        if (!ContainerNumber.IsWellFormed(containerNo))
            return TosSupport.Invalid("containerNo", $"'{request.ContainerNo}' is not a container number.");

        // You survey a box that is HERE: the open stay is what the survey attaches to,
        // and it is what a later repair or release reads back.
        var visit = await db.ContainerVisits.SingleOrDefaultAsync(v => v.ContainerNo == containerNo && v.GateOutTransactionId == null, ct);
        if (visit is null)
            return TosSupport.Invalid("containerNo", $"{containerNo} is not in the yard. A survey belongs to a stay.");
        if (!scope.HasAt(TosPermissions.GateCreate, visit.BranchId))
            return TosScope.OutsideYourBranches($"{containerNo} is at a depot you do not cover.");

        var errors = new Dictionary<string, List<string>>();
        var codes = await master.SurveyCodesAsync(ct);
        var damages = request.Damages ?? [];

        for (var i = 0; i < damages.Count; i++)
        {
            var damage = damages[i];
            var key = $"damages[{i}]";
            if (!codes.DamageCodes.ContainsKey(damage.DamageCode.Clean() ?? ""))
                errors.Add($"{key}.damageCode", $"'{damage.DamageCode}' is not a damage code. CEDEX codes come from master data.");
            if (damage.ComponentCode.Clean() is { } component && !codes.Components.Contains(component))
                errors.Add($"{key}.componentCode", $"'{damage.ComponentCode}' is not a component.");
            if (damage.LocationCode.Clean() is { } location && !codes.Locations.Contains(location))
                errors.Add($"{key}.locationCode", $"'{damage.LocationCode}' is not a damage location.");
            if (damage.Quantity <= 0) errors.Add($"{key}.quantity", "At least one.");
        }

        if (request.GateTransactionId is { } transactionId)
        {
            var transaction = await db.GateTransactions.AsNoTracking()
                .SingleOrDefaultAsync(g => g.GateTransactionId == transactionId && g.ContainerNo == containerNo, ct);
            if (transaction is null) errors.Add("gateTransactionId", "That EIR is not this box's.");
            else if (await db.Surveys.AnyAsync(s => s.GateTransactionId == transactionId, ct))
                errors.Add("gateTransactionId", "That move is already surveyed. A second look is a RE_SURVEY, which is its own record.");
        }

        if (errors.Count > 0) return TosSupport.Invalid(errors);

        // The CODE decides, not the tick box: a hole in a door panel is unserviceable
        // whether or not the surveyor remembered to say so.
        var unserviceable = damages.Any(d => codes.DamageCodes[d.DamageCode.Clean()!].MakesUnserviceable);
        var at = request.SurveyedAt ?? time.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var survey = new Survey
        {
            TenantId = caller.TenantId(),
            BranchId = visit.BranchId,
            ContainerVisitId = visit.ContainerVisitId,
            GateTransactionId = request.GateTransactionId,
            ContainerNo = containerNo,
            SurveyType = request.SurveyType,
            SurveyedAt = at,
            SurveyedBy = caller.UserId(),
            SurveyorName = request.SurveyorName?.Trim(),
            ConditionCode = request.ConditionCode.Clean(),
            GradeCode = request.GradeCode.Clean(),
            IsServiceable = !unserviceable,
            Remarks = request.Remarks?.Trim(),
        };
        db.Surveys.Add(survey);
        await db.SaveChangesAsync(ct);

        short lineNo = 1;
        foreach (var damage in damages)
            db.SurveyDamages.Add(new SurveyDamage
            {
                TenantId = survey.TenantId,
                SurveyId = survey.SurveyId,
                LineNo = lineNo++,
                DamageCode = damage.DamageCode.Clean()!,
                ComponentCode = damage.ComponentCode.Clean(),
                DamageLocationCode = damage.LocationCode.Clean(),
                LengthCm = damage.LengthCm,
                WidthCm = damage.WidthCm,
                Quantity = damage.Quantity,
                IsPreExisting = damage.IsPreExisting,
                Remarks = damage.Remarks?.Trim(),
            });

        // The stay carries the CURRENT condition and grade; the survey is why it changed.
        var previousGrade = visit.GradeCode;
        if (survey.ConditionCode is not null) visit.ConditionCode = survey.ConditionCode;
        if (survey.GradeCode is not null) visit.GradeCode = survey.GradeCode;
        visit.LastEventAt = at;

        db.VisitEvents.Add(new VisitEvent
        {
            TenantId = survey.TenantId,
            ContainerVisitId = visit.ContainerVisitId,
            EventType = "SURVEY",
            FromValue = previousGrade,
            ToValue = survey.GradeCode ?? previousGrade,
            EventAt = at,
            EventBy = caller.UserId(),
            ReferenceId = survey.SurveyId,
            Remarks = unserviceable ? "Unserviceable" : null,
        });

        // ── the hold the depot configured for exactly this ──────────────────
        var applied = new List<string>();
        if (unserviceable)
        {
            var damageHolds = (await master.ActiveHoldsAsync(ct))
                .Where(h => string.Equals(h.AutoApplyOnEvent, DamagedEvent, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var hold in damageHolds)
            {
                if (await db.ContainerHolds.AnyAsync(h => h.ContainerNo == containerNo && h.HoldCode == hold.HoldCode && h.ReleasedAt == null, ct))
                    continue;

                db.ContainerHolds.Add(new ContainerHold
                {
                    TenantId = survey.TenantId,
                    ContainerNo = containerNo,
                    HoldId = hold.HoldId,
                    HoldCode = hold.HoldCode,
                    AppliedAt = at,
                    AppliedBy = caller.UserId(),
                    ApplyReason = $"Survey {survey.SurveyType} found the box unserviceable: {string.Join(", ", damages.Where(d => codes.DamageCodes[d.DamageCode.Clean()!].MakesUnserviceable).Select(d => d.DamageCode.Clean()))}.",
                    Source = "AUTO",
                });
                applied.Add(hold.HoldCode);

                db.VisitEvents.Add(new VisitEvent
                {
                    TenantId = survey.TenantId,
                    ContainerVisitId = visit.ContainerVisitId,
                    EventType = "HOLD_APPLIED",
                    ToValue = hold.HoldCode,
                    EventAt = at,
                    EventBy = caller.UserId(),
                    ReferenceId = survey.SurveyId,
                });
            }
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        var written = await db.SurveyDamages.AsNoTracking().Where(d => d.SurveyId == survey.SurveyId).ToListAsync(ct);
        return TypedResults.Created($"/api/tos/gate/surveys/{survey.SurveyId}", Project(survey, written, codes, applied));
    }

    private static SurveyResponse Project(
        Survey s, IEnumerable<SurveyDamage> damages, SurveyCodeSets codes, IReadOnlyList<string> holdsApplied) => new(
        s.SurveyId, s.ContainerVisitId, s.GateTransactionId, s.ContainerNo, s.BranchId,
        s.SurveyType, s.SurveyedAt, s.SurveyedBy, s.SurveyorName,
        s.ConditionCode, s.GradeCode, s.IsServiceable, s.Remarks,
        damages.OrderBy(d => d.LineNo).Select(d => new SurveyDamageResponse(
            d.SurveyDamageId, d.LineNo, d.DamageLocationCode, d.ComponentCode, d.DamageCode,
            codes.DamageCodes.GetValueOrDefault(d.DamageCode)?.DescriptionEn,
            codes.DamageCodes.GetValueOrDefault(d.DamageCode)?.MakesUnserviceable ?? false,
            d.LengthCm, d.WidthCm, d.Quantity, d.IsPreExisting, d.Remarks)).ToList(),
        holdsApplied, Convert.ToBase64String(s.RowVersion));
}
