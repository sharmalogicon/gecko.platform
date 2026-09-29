using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static Gecko.MasterData.Endpoints.Logistics.LogisticsSupport;

namespace Gecko.MasterData.Endpoints.Parties;

public sealed record SealRangeResponse(
    Guid SealRangeId, Guid BranchId, string PartyCode, string PartyName, string SealPrefix, long SeriesStart, long SeriesEnd,
    byte? NumberLength, long? LastIssuedNumber, long Remaining, DateOnly? ReceivedOn, bool IsActive, string RowVersion);

public sealed record SaveSealRangeRequest(
    Guid BranchId,
    [property: Required, MaxLength(60)] string PartyCode,
    [property: Range(0, 999_999_999_999)] long SeriesStart,
    [property: Range(0, 999_999_999_999)] long SeriesEnd,
    [property: MaxLength(10), RegularExpression("^[A-Za-z0-9-]*$", ErrorMessage = "Letters, digits and '-'.")] string? SealPrefix = null,
    [property: Range(1, 18)] byte? NumberLength = null,
    long? LastIssuedNumber = null,
    DateOnly? ReceivedOn = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// Seal series (party.seal_range) — the blocks of seal numbers a shipping line
/// hands a depot. The gate checks a seal against them; the depot may issue from
/// one. A range belongs to one depot, so the grant is mdm.party.manage AT that
/// depot. Two live ranges with the same prefix may not overlap — at any depot,
/// because a seal number is one physical seal. A range seals were issued from
/// can only be deactivated.
/// </summary>
internal static class SealRangeEndpoints
{
    public static RouteGroupBuilder MapSealRangeEndpoints(this RouteGroupBuilder master)
    {
        var ranges = master.MapGroup("/seal-ranges").WithTags("Master data — seal series");
        ranges.MapGet("/", ListAsync).RequireBranchPermission(MasterDataPermissions.PartyView).WithSummary("List seal ranges at the depots the caller may see");
        ranges.MapPost("/", CreateAsync).RequireBranchPermission(MasterDataPermissions.PartyManage).Validate<SaveSealRangeRequest>().WithSummary("Add a line's seal range at a depot");
        ranges.MapPut("/{sealRangeId:guid}", UpdateAsync).RequireBranchPermission(MasterDataPermissions.PartyManage).Validate<SaveSealRangeRequest>().WithSummary("Update a seal range (optimistic concurrency on rowVersion)");
        ranges.MapDelete("/{sealRangeId:guid}", DeleteAsync).RequireBranchPermission(MasterDataPermissions.PartyManage).WithSummary("Soft-delete a seal range no seal was issued from (?rowVersion=)");
        return master;
    }

    private static IQueryable<SealRangeResponse> Rows(MasterDataDbContext db, IQueryable<SealRange> ranges) =>
        from r in ranges
        join p in db.Parties.AsNoTracking() on r.PartyId equals p.PartyId
        select new SealRangeResponse(
            r.SealRangeId, r.BranchId, p.PartyCode, p.NameEn, r.SealPrefix, r.SeriesStart, r.SeriesEnd, r.NumberLength,
            r.LastIssuedNumber, r.SeriesEnd - (r.LastIssuedNumber ?? r.SeriesStart - 1), r.ReceivedOn, r.IsActive,
            Convert.ToBase64String(r.RowVersion));

    private static async Task<Ok<IReadOnlyList<SealRangeResponse>>> ListAsync(
        MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? partyCode = null, bool includeInactive = false)
    {
        var ranges = db.SealRanges.AsNoTracking();
        // Only the depots the caller may see: a branch-scoped clerk sees their own depot's ranges.
        if (scope.BranchesFor(MasterDataPermissions.PartyView) is { } branches) ranges = ranges.Where(r => branches.Contains(r.BranchId));
        if (branchId is { } b) ranges = ranges.Where(r => r.BranchId == b);
        if (!includeInactive) ranges = ranges.Where(r => r.IsActive);
        if (Upper(partyCode) is { } code) ranges = ranges.Where(r => db.Parties.Any(p => p.PartyId == r.PartyId && p.PartyCode == code));
        return TypedResults.Ok<IReadOnlyList<SealRangeResponse>>(
            await Rows(db, ranges.OrderBy(r => r.SealPrefix).ThenBy(r => r.SeriesStart)).ToListAsync(ct));
    }

    private static async Task<Results<Created<SealRangeResponse>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveSealRangeRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (!scope.HasAt(MasterDataPermissions.PartyManage, request.BranchId)) return TypedResults.Forbid();
        var (partyId, invalid) = await ValidateAsync(db, request, null, ct);
        if (invalid is not null) return invalid;

        var range = new SealRange { TenantId = caller.TenantId(), BranchId = request.BranchId };
        Apply(range, request, partyId!.Value);
        db.SealRanges.Add(range);
        await db.SaveChangesAsync(ct);
        var saved = await Rows(db, db.SealRanges.AsNoTracking().Where(r => r.SealRangeId == range.SealRangeId)).SingleAsync(ct);
        return TypedResults.Created($"/api/master/seal-ranges/{range.SealRangeId}", saved);
    }

    private static async Task<Results<Ok<SealRangeResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid sealRangeId, SaveSealRangeRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var range = await db.SealRanges.SingleOrDefaultAsync(r => r.SealRangeId == sealRangeId, ct);
        if (range is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.PartyManage, range.BranchId)) return TypedResults.Forbid();
        if (request.BranchId != range.BranchId)
            return MasterDataSupport.InvalidReference("branchId", "A seal range stays at its depot. Add a new range at the other depot instead.");
        if (db.ExpectVersion(range, request.RowVersion) is { } missing) return missing;
        // Stale first: the checks below compare with the row as it is NOW (the
        // issued counter), which a caller holding an old version never saw.
        if (request.RowVersion != Convert.ToBase64String(range.RowVersion))
            return MasterDataSupport.Conflict("The record changed since you loaded it.",
                "Re-read the seal range and re-apply your change. The rowVersion you sent is no longer current.");
        var (partyId, invalid) = await ValidateAsync(db, request, range, ct);
        if (invalid is not null) return invalid;

        Apply(range, request, partyId!.Value);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Rows(db, db.SealRanges.AsNoTracking().Where(r => r.SealRangeId == sealRangeId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        Guid sealRangeId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var range = await db.SealRanges.SingleOrDefaultAsync(r => r.SealRangeId == sealRangeId, ct);
        if (range is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.PartyManage, range.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(range, rowVersion) is { } missing) return missing;
        if (range.LastIssuedNumber is not null)
            return MasterDataSupport.Conflict("Seals were issued from this range.", "Set it inactive instead, so the issued seals still trace back to it.");
        db.SealRanges.Remove(range);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<(Guid? PartyId, ValidationProblem? Invalid)> ValidateAsync(
        MasterDataDbContext db, SaveSealRangeRequest r, SealRange? existing, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var partyId = await PartyAsync(db, "partyCode", Upper(r.PartyCode), lineOnly: true, errors, ct);
        if (!await db.BranchProfiles.AsNoTracking().AnyAsync(b => b.BranchId == r.BranchId, ct))
            errors["branchId"] = ["Unknown depot for this tenant."];

        if (r.SeriesEnd < r.SeriesStart) errors["seriesEnd"] = ["The last number cannot be below the first."];
        if (r.NumberLength is { } len && r.SeriesEnd.ToString().Length > len)
            errors["numberLength"] = [$"{r.SeriesEnd} has more than {len} digits."];
        if (r.LastIssuedNumber is { } issued && (issued < r.SeriesStart || issued > r.SeriesEnd))
            errors["lastIssuedNumber"] = [$"The last issued seal must be inside the range ({r.SeriesStart}–{r.SeriesEnd})."];
        if (existing?.LastIssuedNumber is { } wasIssued && (r.LastIssuedNumber ?? -1) < wasIssued)
            errors["lastIssuedNumber"] = [$"Seals up to {wasIssued} were already issued; the counter cannot go back."];

        if (!errors.ContainsKey("seriesEnd"))
        {
            var prefix = Upper(r.SealPrefix) ?? "";
            var clash = await (
                from x in db.SealRanges.AsNoTracking()
                join p in db.Parties.AsNoTracking() on x.PartyId equals p.PartyId
                where x.SealPrefix == prefix && x.IsActive && x.SealRangeId != (existing == null ? Guid.Empty : existing.SealRangeId)
                      && x.SeriesStart <= r.SeriesEnd && x.SeriesEnd >= r.SeriesStart
                select new { p.PartyCode, x.SeriesStart, x.SeriesEnd }).FirstOrDefaultAsync(ct);
            if (clash is not null)
                errors["seriesStart"] = [$"Overlaps {prefix}{clash.SeriesStart}–{prefix}{clash.SeriesEnd} of {clash.PartyCode}. A seal number is one seal."];
        }
        return (partyId, Problem(errors));
    }

    private static void Apply(SealRange s, SaveSealRangeRequest r, Guid partyId)
    {
        s.PartyId = partyId;
        s.SealPrefix = Upper(r.SealPrefix) ?? "";
        s.SeriesStart = r.SeriesStart;
        s.SeriesEnd = r.SeriesEnd;
        s.NumberLength = r.NumberLength;
        s.LastIssuedNumber = r.LastIssuedNumber;
        s.ReceivedOn = r.ReceivedOn;
        s.IsActive = r.IsActive;
    }
}
