using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Gecko.Data;
using Gecko.Identity.Contracts;
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

/// <summary>
/// An after-Save correction (owner D1). Each field: null = unchanged; "" = cleared. <see cref="Seals"/>: null =
/// unchanged; a list (even empty) replaces the EIR's seals.
/// </summary>
public sealed record GateCorrectionRequest(
    [property: Required, MinLength(3), MaxLength(300)] string? Reason,
    IReadOnlyList<SealRequest>? Seals = null,
    [property: MaxLength(500)] string? Remarks = null,
    [property: MaxLength(40)] string? CustomsPermitNo = null,
    [property: MaxLength(20)] string? ClipOnNo = null);

public sealed record GateCorrectedFields(IReadOnlyList<GateSealResponse> Seals, string? Remarks, string? CustomsPermitNo, string? ClipOnNo);

public sealed record GateCorrectionResponse(
    Guid GateTransactionCorrectionId, Guid GateTransactionId, string EirNo, string Reason,
    DateTimeOffset CorrectedAt, Guid CorrectedBy, string? CorrectedByName,
    GateCorrectedFields Before, GateCorrectedFields After);

/// <summary>
/// GATE_IN_COMPLETION_PLAN A9 (owner D1; Vector "Update Details", GateIn.cs:2371, 3659): after Save the clerk
/// corrects a LIVE EIR's seals, remarks, customs permit no and clip-on no — nothing else (anything else is void
/// and re-record). The EIR is append-only by grant except those columns (gecko_tos 26); each correction keeps
/// what they were and became, why, who and when.
/// </summary>
internal static class CorrectionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapCorrectionEndpoints(this RouteGroupBuilder tos)
    {
        var corrections = tos.MapGroup("/gate/transactions/{id:guid}/corrections").WithTags("TOS — gate");
        corrections.MapPost("/", CorrectAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<GateCorrectionRequest>()
            .WithSummary("Correct a live EIR's seals, remarks, customs permit no or clip-on no, with a reason")
            .WithDescription("Null = unchanged, \"\" = cleared; seals given replace the EIR's seals. Anything else: void the EIR and record the move again.");
        corrections.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The corrections of one EIR, oldest first");
        return tos;
    }

    private static async Task<Results<Created<GateCorrectionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CorrectAsync(
        Guid id, GateCorrectionRequest request, TosDbContext db, ITenantContext caller, ICallerPermissions scope,
        IUserDirectory users, TimeProvider time, CancellationToken ct)
    {
        var transaction = await db.GateTransactions.SingleOrDefaultAsync(g => g.GateTransactionId == id, ct);
        if (transaction is null || !scope.HasAt(TosPermissions.GateCreate, transaction.BranchId)) return TypedResults.NotFound();
        if (transaction.Status != "COMPLETED")
            return TosSupport.Conflict($"{transaction.EirNo} is {transaction.Status}.", "A voided EIR is not corrected: record the move again.");

        var seals = await db.GateTransactionSeals.Where(s => s.GateTransactionId == id && s.DeletedAt == null).ToListAsync(ct);
        var before = Fields(seals.Select(Seal), transaction.Remarks, transaction.CustomsPermitNo, transaction.ClipOnNo);

        static string? Changed(string? sent, string? now) => sent is null ? now : sent.Trim() is { Length: > 0 } v ? v : null;
        var newSeals = request.Seals?.Select(s => new GateSealResponse(s.SealNo.Trim(), s.SealType, s.IsIntact, null)).ToList();
        var after = Fields(newSeals ?? before.Seals,
            Changed(request.Remarks, transaction.Remarks),
            Changed(request.CustomsPermitNo, transaction.CustomsPermitNo)?.ToUpperInvariant(),
            Changed(request.ClipOnNo, transaction.ClipOnNo)?.ToUpperInvariant());
        if (newSeals is not null && newSeals.GroupBy(s => (s.SealType, s.SealNo.ToUpperInvariant())).Any(g => g.Count() > 1))
            return TosSupport.Invalid("seals", "The same seal twice.");
        if (Same(before, after))
            return TosSupport.Invalid("reason", "Nothing to correct: every field is as the EIR already says.");

        var declared = await db.BookingContainers.AsNoTracking().Where(x => x.BookingContainerId == transaction.BookingContainerId)
            .Select(x => x.DeclaredSealNo).SingleOrDefaultAsync(ct);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        transaction.Remarks = after.Remarks;
        transaction.CustomsPermitNo = after.CustomsPermitNo;
        transaction.ClipOnNo = after.ClipOnNo;
        if (newSeals is not null)
        {
            foreach (var old in seals)
            {
                old.DeletedAt = now;
                old.DeletedBy = caller.UserId();
            }
            // The seals go first: the new set may repeat a number the old one had (one live seal per number).
            if (await db.SaveOrConflictAsync(ct) is { } sealConflict) return sealConflict;
            foreach (var seal in newSeals)
                db.GateTransactionSeals.Add(new GateTransactionSeal
                {
                    TenantId = transaction.TenantId,
                    GateTransactionId = id,
                    SealNo = seal.SealNo,
                    SealType = seal.SealType,
                    IsIntact = seal.IsIntact,
                    MatchesDeclared = string.IsNullOrWhiteSpace(declared) ? null : string.Equals(seal.SealNo, declared.Trim(), StringComparison.OrdinalIgnoreCase),
                });
        }

        var correction = new GateTransactionCorrection
        {
            TenantId = transaction.TenantId,
            GateTransactionId = id,
            Reason = request.Reason!.Trim(),
            BeforeJson = JsonSerializer.Serialize(before, Json),
            AfterJson = JsonSerializer.Serialize(after, Json),
            CorrectedAt = now,
            CorrectedBy = caller.UserId(),
            CreatedBy = caller.UserId(),
            UpdatedBy = caller.UserId(),
        };
        db.GateTransactionCorrections.Add(correction);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        var name = (await users.DisplayNamesAsync([correction.CorrectedBy], ct)).GetValueOrDefault(correction.CorrectedBy);
        return TypedResults.Created($"/api/tos/gate/transactions/{id}/corrections", Project(correction, transaction.EirNo, name));
    }

    private static async Task<Results<Ok<List<GateCorrectionResponse>>, NotFound>> ListAsync(
        Guid id, TosDbContext db, ICallerPermissions scope, IUserDirectory users, CancellationToken ct)
    {
        var transaction = await db.GateTransactions.AsNoTracking().Where(g => g.GateTransactionId == id)
            .Select(g => new { g.BranchId, g.EirNo }).SingleOrDefaultAsync(ct);
        if (transaction is null || !scope.HasAt(TosPermissions.GateView, transaction.BranchId)) return TypedResults.NotFound();

        var rows = await db.GateTransactionCorrections.AsNoTracking().Where(c => c.GateTransactionId == id)
            .OrderBy(c => c.CorrectedAt).ToListAsync(ct);
        var names = await users.DisplayNamesAsync(rows.Select(r => r.CorrectedBy).Distinct().ToList(), ct);
        return TypedResults.Ok(rows.Select(r => Project(r, transaction.EirNo, names.GetValueOrDefault(r.CorrectedBy))).ToList());
    }

    private static GateSealResponse Seal(GateTransactionSeal s) => new(s.SealNo, s.SealType, s.IsIntact, s.MatchesDeclared);

    private static GateCorrectedFields Fields(IEnumerable<GateSealResponse> seals, string? remarks, string? permit, string? clipOn) =>
        new(seals.OrderBy(s => s.SealType).ThenBy(s => s.SealNo).ToList(), remarks, permit, clipOn);

    private static bool Same(GateCorrectedFields a, GateCorrectedFields b) =>
        a.Remarks == b.Remarks && a.CustomsPermitNo == b.CustomsPermitNo && a.ClipOnNo == b.ClipOnNo
        && a.Seals.Select(s => (s.SealType, s.SealNo.ToUpperInvariant(), s.IsIntact))
            .SequenceEqual(b.Seals.Select(s => (s.SealType, s.SealNo.ToUpperInvariant(), s.IsIntact)));

    private static GateCorrectionResponse Project(GateTransactionCorrection c, string eirNo, string? name) => new(
        c.GateTransactionCorrectionId, c.GateTransactionId, eirNo, c.Reason, c.CorrectedAt, c.CorrectedBy, name,
        JsonSerializer.Deserialize<GateCorrectedFields>(c.BeforeJson, Json)!,
        JsonSerializer.Deserialize<GateCorrectedFields>(c.AfterJson, Json)!);
}
