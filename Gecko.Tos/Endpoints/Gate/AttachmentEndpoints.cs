using Gecko.Data;
using Gecko.Data.Documents;
using Gecko.SharedKernel;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

public sealed record AttachmentResponse(
    Guid AttachmentId, string OwnerType, Guid OwnerId, string ContentType, long? SizeBytes,
    string? Sha256, string? Caption, DateTimeOffset? TakenAt, DateTimeOffset CreatedAt);

/// <summary>
/// Photos and scans at the gate (PLAN 5.6): the damage the surveyor saw, the
/// seal, the slip the driver handed over. Attached to an EIR, a survey, one damage
/// line or a truck visit.
///
/// The bytes go to <see cref="IFileStore"/>; gate.attachment keeps the reference,
/// the size and the SHA-256, so a file swapped afterwards is detectable. A photo
/// is evidence in a damage claim, so "delete" hides it and never destroys it.
/// </summary>
internal static class AttachmentEndpoints
{
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp",
        ["image/heic"] = ".heic", ["application/pdf"] = ".pdf",
    };

    private static readonly string[] Owners = ["GATE_TRANSACTION", "SURVEY", "SURVEY_DAMAGE", "TRUCK_VISIT"];

    public static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder tos)
    {
        var attachments = tos.MapGroup("/gate/attachments").WithTags("TOS — gate attachments");

        attachments.MapPost("/", UploadAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .DisableAntiforgery()   // bearer-token API: no cookie for a forged form to ride on
            .WithSummary("Attach a photo or PDF (≤ 10 MB) to an EIR, a survey, a damage line or a truck visit");
        attachments.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The attachments of one owner");
        attachments.MapGet("/{id:guid}/content", ContentAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The file itself");
        attachments.MapDelete("/{id:guid}", DeleteAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .WithSummary("Hide an attachment (the file is kept: it is evidence)");

        return tos;
    }

    private static async Task<Results<Created<AttachmentResponse>, NotFound, ValidationProblem>> UploadAsync(
        [FromForm] string ownerType, [FromForm] Guid ownerId, IFormFile? file, [FromForm] string? caption, [FromForm] DateTimeOffset? takenAt,
        TosDbContext db, IFileStore store, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var owner = ownerType?.Trim().ToUpperInvariant() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!Owners.Contains(owner)) errors["ownerType"] = [$"One of {string.Join(", ", Owners)}."];
        if (file is null || file.Length == 0) errors["file"] = ["Attach a file."];
        else if (file.Length > MaxBytes) errors["file"] = [$"At most {MaxBytes / 1024 / 1024} MB."];
        else if (!Allowed.ContainsKey(file.ContentType ?? "")) errors["file"] = [$"'{file.ContentType}' is not accepted: {string.Join(", ", Allowed.Keys)}."];
        if (caption?.Length > 200) errors["caption"] = ["At most 200 characters."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        if (await OwnerBranchAsync(db, owner, ownerId, ct) is not { } branchId || !scope.HasAt(TosPermissions.GateCreate, branchId))
            return TypedResults.NotFound();

        StoredFile stored;
        await using (var content = file!.OpenReadStream())
            stored = await store.SaveAsync(caller.TenantId(), "gate", Allowed[file.ContentType!], content, ct);

        var row = new Attachment
        {
            AttachmentId = Guid.CreateVersion7(), TenantId = caller.TenantId(), OwnerType = owner, OwnerId = ownerId,
            BlobUri = stored.Uri, ContentType = file.ContentType!.ToLowerInvariant(), SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
            Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(), TakenAt = takenAt, TakenBy = caller.UserId,
        };
        db.Attachments.Add(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/tos/gate/attachments/{row.AttachmentId}/content", ToResponse(row));
    }

    private static async Task<Results<Ok<List<AttachmentResponse>>, NotFound>> ListAsync(
        string ownerType, Guid ownerId, TosDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var owner = ownerType.Trim().ToUpperInvariant();
        if (await OwnerBranchAsync(db, owner, ownerId, ct) is not { } branchId || !scope.HasAt(TosPermissions.GateView, branchId))
            return TypedResults.NotFound();

        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == owner && a.OwnerId == ownerId)
            .OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToResponse).ToList());
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> ContentAsync(
        Guid id, TosDbContext db, IFileStore store, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.AttachmentId == id, ct);
        if (row is null || await OwnerBranchAsync(db, row.OwnerType, row.OwnerId, ct) is not { } branchId
            || !scope.HasAt(TosPermissions.GateView, branchId))
            return TypedResults.NotFound();

        var stream = await store.OpenAsync(caller.TenantId(), row.BlobUri, ct);
        return stream is null
            ? TypedResults.NotFound()
            : TypedResults.Stream(stream, row.ContentType, $"{row.OwnerType.ToLowerInvariant()}-{row.AttachmentId:N}{Allowed.GetValueOrDefault(row.ContentType, "")}");
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id, TosDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var row = await db.Attachments.SingleOrDefaultAsync(a => a.AttachmentId == id, ct);
        if (row is null || await OwnerBranchAsync(db, row.OwnerType, row.OwnerId, ct) is not { } branchId
            || !scope.HasAt(TosPermissions.GateCreate, branchId))
            return TypedResults.NotFound();

        db.Attachments.Remove(row);   // AuditStampInterceptor turns this into a soft delete
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The branch an owner belongs to — which is also the check that it exists in this tenant (RLS).</summary>
    private static async Task<Guid?> OwnerBranchAsync(TosDbContext db, string ownerType, Guid ownerId, CancellationToken ct) => ownerType switch
    {
        "GATE_TRANSACTION" => await db.GateTransactions.AsNoTracking().Where(x => x.GateTransactionId == ownerId).Select(x => (Guid?)x.BranchId).SingleOrDefaultAsync(ct),
        "SURVEY" => await db.Surveys.AsNoTracking().Where(x => x.SurveyId == ownerId).Select(x => (Guid?)x.BranchId).SingleOrDefaultAsync(ct),
        "SURVEY_DAMAGE" => await (from d in db.SurveyDamages.AsNoTracking() join s in db.Surveys on d.SurveyId equals s.SurveyId
                                  where d.SurveyDamageId == ownerId select (Guid?)s.BranchId).SingleOrDefaultAsync(ct),
        "TRUCK_VISIT" => await db.TruckVisits.AsNoTracking().Where(x => x.TruckVisitId == ownerId).Select(x => (Guid?)x.BranchId).SingleOrDefaultAsync(ct),
        _ => null,
    };

    private static AttachmentResponse ToResponse(Attachment a) =>
        new(a.AttachmentId, a.OwnerType, a.OwnerId, a.ContentType, a.SizeBytes,
            a.Sha256 is null ? null : Convert.ToHexString(a.Sha256).ToLowerInvariant(), a.Caption, a.TakenAt, a.CreatedAt);
}
