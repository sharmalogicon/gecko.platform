using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Application;
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

public sealed record ContainerResponse(
    Guid ContainerId, string ContainerNo, string Prefix,
    Guid? EquipmentTypeId, string? TypeCode, string? IsoCode,
    Guid? OwnerPartyId, string? OwnerCode, Guid? LessorPartyId, string? LessorCode,
    string OwnershipType, string? Material, DateOnly? ManufactureDate, string? Manufacturer,
    string? CscPlateRef, string? AcepRef, DateOnly? NextExaminationDate,
    decimal? TareWeightKg, decimal? MaxGrossKg,
    string? ReeferUnitMake, string? ReeferUnitModel,
    string Status, DateTimeOffset? StatusChangedAt, bool IsCheckDigitValid,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);

/// <summary>
/// What a gate asks about a number it has just scanned. Deliberately answers for
/// numbers the depot has never seen — "unknown" and "malformed" are different
/// problems with different handling.
/// </summary>
public sealed record ContainerValidationResponse(
    string ContainerNo, bool IsWellFormed, bool IsCheckDigitValid, int? ExpectedCheckDigit,
    string? Prefix, bool PrefixIsRegistered, string? PrefixOwnerCode,
    bool IsKnown, Guid? ContainerId, string? TypeCode, string? Status,
    bool WouldBeAcceptedAtGate, string? Note);

public sealed record CreateContainerRequest(
    [property: Required, RegularExpression("^[A-Za-z]{3}[UuJjZz][0-9]{7}$", ErrorMessage = "ISO 6346 format: 3 letters, U/J/Z, then 7 digits — e.g. MSKU1234567.")] string ContainerNo,
    [property: Required, AllowedValues("LINE_OWNED", "LEASED", "SHIPPER_OWNED", "DEPOT_OWNED")] string OwnershipType,
    Guid? EquipmentTypeId = null,
    [property: RegularExpression("^[A-Za-z0-9]{4}$")] string? IsoCode = null,
    Guid? OwnerPartyId = null,
    Guid? LessorPartyId = null,
    // null belongs in the list: AllowedValues does NOT exempt a nullable field, so
    // omitting `material` fails validation unless null is an allowed value.
    [property: AllowedValues(null, "STEEL", "CORTEN_STEEL", "ALUMINIUM", "STAINLESS_STEEL", "FRP", "OTHER")] string? Material = null,
    DateOnly? ManufactureDate = null,
    [property: MaxLength(100)] string? Manufacturer = null,
    [property: MaxLength(50)] string? CscPlateRef = null,
    [property: MaxLength(50)] string? AcepRef = null,
    DateOnly? NextExaminationDate = null,
    [property: Range(1, 50000)] decimal? TareWeightKg = null,
    [property: Range(1, 100000)] decimal? MaxGrossKg = null,
    [property: MaxLength(40)] string? ReeferUnitMake = null,
    [property: MaxLength(40)] string? ReeferUnitModel = null);

public sealed record UpdateContainerRequest(
    [property: Required] string RowVersion,
    [property: Required, AllowedValues("LINE_OWNED", "LEASED", "SHIPPER_OWNED", "DEPOT_OWNED")] string OwnershipType,
    [property: Required, AllowedValues("IN_SERVICE", "OFF_HIRED", "SOLD", "TOTAL_LOSS", "SCRAPPED")] string Status,
    Guid? EquipmentTypeId = null,
    [property: RegularExpression("^[A-Za-z0-9]{4}$")] string? IsoCode = null,
    Guid? OwnerPartyId = null,
    Guid? LessorPartyId = null,
    // null belongs in the list: AllowedValues does NOT exempt a nullable field, so
    // omitting `material` fails validation unless null is an allowed value.
    [property: AllowedValues(null, "STEEL", "CORTEN_STEEL", "ALUMINIUM", "STAINLESS_STEEL", "FRP", "OTHER")] string? Material = null,
    DateOnly? ManufactureDate = null,
    [property: MaxLength(100)] string? Manufacturer = null,
    [property: MaxLength(50)] string? CscPlateRef = null,
    [property: MaxLength(50)] string? AcepRef = null,
    DateOnly? NextExaminationDate = null,
    [property: Range(1, 50000)] decimal? TareWeightKg = null,
    [property: Range(1, 100000)] decimal? MaxGrossKg = null,
    [property: MaxLength(40)] string? ReeferUnitMake = null,
    [property: MaxLength(40)] string? ReeferUnitModel = null);

/// <summary>
/// The container REGISTRY — what a box is. Where it is standing right now, what
/// condition it is in and what holds are on it belong to TOS (ADR-007 survey
/// seam), which is why there is no grade, condition or yard position here.
/// `status` is the LIFECYCLE state — in service, off hired, scrapped — not a
/// location.
///
/// Addressed by CONTAINER NUMBER, not by GUID. The number is what is painted on
/// the box, what the OCR camera reads, what the EDI message carries and what the
/// clerk types; making callers resolve it to a GUID first would add a round trip
/// to every gate transaction for no benefit.
/// </summary>
internal static class ContainerEndpoints
{
    public static RouteGroupBuilder MapContainerEndpoints(this RouteGroupBuilder master)
    {
        var containers = master.MapGroup("/containers").WithTags("Master data — container registry");

        containers.MapGet("/", ListAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("List the container registry");

        containers.MapGet("/validate/{containerNo}", ValidateAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("Validate a scanned container number — works for numbers the depot has never seen");

        containers.MapGet("/{containerNo}", GetAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithName("GetContainer")
            .WithSummary("Get one container by its number");

        containers.MapPost("/", CreateAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .Validate<CreateContainerRequest>()
            .WithSummary("Register a container");

        containers.MapPut("/{containerNo}", UpdateAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .Validate<UpdateContainerRequest>()
            .WithSummary("Update a container (the number itself is immutable)");

        containers.MapDelete("/{containerNo}", DeleteAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .WithSummary("Soft-delete a container from the registry");

        return master;
    }

    private static async Task<Ok<PagedResult<ContainerResponse>>> ListAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct,
        Guid? equipmentTypeId = null, Guid? ownerPartyId = null, string? ownershipType = null, string? status = null)
    {
        var containers = db.Containers.AsNoTracking();

        if (equipmentTypeId is not null) containers = containers.Where(c => c.EquipmentTypeId == equipmentTypeId);
        if (ownerPartyId is not null) containers = containers.Where(c => c.OwnerPartyId == ownerPartyId);
        if (!string.IsNullOrWhiteSpace(ownershipType)) containers = containers.Where(c => c.OwnershipType == ownershipType.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(status)) containers = containers.Where(c => c.Status == status.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // A clerk searching by prefix ("MRKU") and by full number are the same
            // gesture, so both are a contains-match on the number.
            var search = ContainerNumber.Normalise(query.Search);
            containers = containers.Where(c => c.ContainerNo.Contains(search));
        }

        return TypedResults.Ok(await Project(db, containers.OrderBy(c => c.ContainerNo))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<ContainerResponse>, NotFound>> GetAsync(
        string containerNo, MasterDataDbContext db, CancellationToken ct)
    {
        var number = ContainerNumber.Normalise(containerNo);
        return await Project(db, db.Containers.AsNoTracking().Where(c => c.ContainerNo == number))
            .SingleOrDefaultAsync(ct) is { } container
            ? TypedResults.Ok(container)
            : TypedResults.NotFound();
    }

    /// <summary>
    /// The gate's actual question, answered in one call and without requiring the
    /// box to be registered. Four distinct outcomes, and conflating any two of
    /// them produces a bad gate experience:
    ///   malformed        -> the clerk typed it wrong, ask again
    ///   bad check digit  -> probably an OCR misread, ask for a re-scan
    ///   unknown but valid-> a genuine box the depot has not seen; register it
    ///   known            -> proceed, and here is the type
    /// </summary>
    private static async Task<Ok<ContainerValidationResponse>> ValidateAsync(
        string containerNo, MasterDataDbContext db, TenantSettingReader settings, CancellationToken ct,
        Guid? branchId = null)
    {
        var number = ContainerNumber.Normalise(containerNo);
        var wellFormed = ContainerNumber.IsWellFormed(number);
        var expected = ContainerNumber.CheckDigitOf(number);
        var checkDigitValid = ContainerNumber.IsValid(number);
        var prefix = ContainerNumber.PrefixOf(number);

        var registeredPrefix = prefix is null ? null : await db.ContainerPrefixes.AsNoTracking()
            .Where(p => p.Prefix == prefix)
            .Join(db.Parties, p => p.PartyId, party => party.PartyId, (p, party) => party.PartyCode)
            .SingleOrDefaultAsync(ct);

        var known = await db.Containers.AsNoTracking()
            .Where(c => c.ContainerNo == number)
            .Select(c => new { c.ContainerId, c.Status, c.EquipmentTypeId })
            .SingleOrDefaultAsync(ct);

        var typeCode = known?.EquipmentTypeId is null ? null : await db.EquipmentTypes.AsNoTracking()
            .Where(t => t.EquipmentTypeId == known.EquipmentTypeId)
            .Select(t => t.TypeCode)
            .SingleOrDefaultAsync(ct);

        var enforceCheckDigit = await settings.GetBoolAsync(MasterDataSettings.EnforceCheckDigit, branchId, true, ct);
        var enforcePrefix = await settings.GetBoolAsync(MasterDataSettings.EnforceContainerPrefix, branchId, false, ct);

        var accepted = wellFormed
            && (checkDigitValid || !enforceCheckDigit)
            && (registeredPrefix is not null || !enforcePrefix);

        var note =
            !wellFormed ? "Not an ISO 6346 container number — check the owner code and the 7-digit serial."
            : !checkDigitValid && enforceCheckDigit ? $"Check digit is wrong; expected {expected}. Usually an OCR misread — re-scan before overriding."
            : !checkDigitValid ? $"Check digit is wrong (expected {expected}), but this tenant does not enforce it."
            : enforcePrefix && registeredPrefix is null ? $"Prefix '{prefix}' is not registered to any party, and this tenant enforces prefixes."
            : known is null ? "Valid number, not yet in the registry. It can be registered on the fly."
            : null;

        return TypedResults.Ok(new ContainerValidationResponse(
            number, wellFormed, checkDigitValid, expected,
            prefix, registeredPrefix is not null, registeredPrefix,
            known is not null, known?.ContainerId, typeCode, known?.Status,
            accepted, note));
    }

    private static async Task<Results<CreatedAtRoute<ContainerResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateContainerRequest request, MasterDataDbContext db, ITenantContext caller,
        TenantSettingReader settings, CancellationToken ct)
    {
        var number = ContainerNumber.Normalise(request.ContainerNo);
        var checkDigitValid = ContainerNumber.IsValid(number);

        if (await ValidateReferencesAsync(db, settings, number, checkDigitValid, request.OwnershipType,
                request.EquipmentTypeId, request.IsoCode, request.OwnerPartyId, request.LessorPartyId, ct) is { } problem)
            return problem;

        if (await db.Containers.AnyAsync(c => c.ContainerNo == number, ct))
            return MasterDataSupport.Conflict($"Container '{number}' is already in the registry.");

        var container = new Container
        {
            TenantId = caller.TenantId(),
            ContainerNo = number,
            EquipmentTypeId = request.EquipmentTypeId,
            IsoCode = request.IsoCode?.ToUpperInvariant(),
            OwnerPartyId = request.OwnerPartyId,
            LessorPartyId = request.LessorPartyId,
            OwnershipType = request.OwnershipType,
            Material = request.Material,
            ManufactureDate = request.ManufactureDate,
            Manufacturer = request.Manufacturer,
            CscPlateRef = request.CscPlateRef,
            AcepRef = request.AcepRef,
            NextExaminationDate = request.NextExaminationDate,
            TareWeightKg = request.TareWeightKg,
            MaxGrossKg = request.MaxGrossKg,
            ReeferUnitMake = request.ReeferUnitMake,
            ReeferUnitModel = request.ReeferUnitModel,
            Status = "IN_SERVICE",
            StatusChangedAt = DateTimeOffset.UtcNow,
            // Recorded even when the tenant does not enforce it, so a depot that
            // later turns enforcement on can find the boxes it already accepted.
            IsCheckDigitValid = checkDigitValid,
        };

        db.Containers.Add(container);
        await db.SaveChangesAsync(ct);

        var response = await Project(db, db.Containers.AsNoTracking().Where(c => c.ContainerId == container.ContainerId))
            .SingleAsync(ct);
        return TypedResults.CreatedAtRoute(response, "GetContainer", new { containerNo = number });
    }

    private static async Task<Results<Ok<ContainerResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string containerNo, UpdateContainerRequest request, MasterDataDbContext db,
        TenantSettingReader settings, CancellationToken ct)
    {
        var number = ContainerNumber.Normalise(containerNo);
        var container = await db.Containers.SingleOrDefaultAsync(c => c.ContainerNo == number, ct);
        if (container is null) return TypedResults.NotFound();

        if (!db.TrySetExpectedVersion(container, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        if (await ValidateReferencesAsync(db, settings, number, container.IsCheckDigitValid, request.OwnershipType,
                request.EquipmentTypeId, request.IsoCode, request.OwnerPartyId, request.LessorPartyId, ct) is { } problem)
            return problem;

        // status_changed_at is the answer to "when did this box leave the fleet",
        // so it only moves when the status actually moves.
        if (!string.Equals(container.Status, request.Status, StringComparison.Ordinal))
        {
            container.Status = request.Status;
            container.StatusChangedAt = DateTimeOffset.UtcNow;
        }

        container.EquipmentTypeId = request.EquipmentTypeId;
        container.IsoCode = request.IsoCode?.ToUpperInvariant();
        container.OwnerPartyId = request.OwnerPartyId;
        container.LessorPartyId = request.LessorPartyId;
        container.OwnershipType = request.OwnershipType;
        container.Material = request.Material;
        container.ManufactureDate = request.ManufactureDate;
        container.Manufacturer = request.Manufacturer;
        container.CscPlateRef = request.CscPlateRef;
        container.AcepRef = request.AcepRef;
        container.NextExaminationDate = request.NextExaminationDate;
        container.TareWeightKg = request.TareWeightKg;
        container.MaxGrossKg = request.MaxGrossKg;
        container.ReeferUnitMake = request.ReeferUnitMake;
        container.ReeferUnitModel = request.ReeferUnitModel;

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(await Project(db, db.Containers.AsNoTracking().Where(c => c.ContainerId == container.ContainerId))
            .SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        string containerNo, MasterDataDbContext db, CancellationToken ct)
    {
        var number = ContainerNumber.Normalise(containerNo);
        var container = await db.Containers.SingleOrDefaultAsync(c => c.ContainerNo == number, ct);
        if (container is null) return TypedResults.NotFound();

        // No reference check against TOS: this database cannot see gate moves, and
        // a cross-context query would be the wrong answer anyway (ADR-007). Soft
        // delete keeps the row and its history for anything that still points here.
        db.Containers.Remove(container);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Every soft ref and every CHECK constraint this table carries, resolved to a
    /// field-level 400 instead of a raw SQL error. There are no foreign keys, so
    /// this is the only place a wrong id gets caught.
    /// </summary>
    private static async Task<ValidationProblem?> ValidateReferencesAsync(
        MasterDataDbContext db, TenantSettingReader settings, string number, bool checkDigitValid,
        string ownershipType, Guid? equipmentTypeId, string? isoCode, Guid? ownerPartyId, Guid? lessorPartyId,
        CancellationToken ct)
    {
        if (!ContainerNumber.IsWellFormed(number))
            return MasterDataSupport.InvalidReference("containerNo", "Not an ISO 6346 container number.");

        if (!checkDigitValid && await settings.GetBoolAsync(MasterDataSettings.EnforceCheckDigit, null, true, ct))
            return MasterDataSupport.InvalidReference("containerNo",
                $"Check digit is wrong; expected {ContainerNumber.CheckDigitOf(number)}. " +
                $"Set {MasterDataSettings.EnforceCheckDigit} = false if this depot accepts mis-stencilled boxes.");

        if (await settings.GetBoolAsync(MasterDataSettings.EnforceContainerPrefix, null, false, ct))
        {
            var prefix = ContainerNumber.PrefixOf(number);
            if (!await db.ContainerPrefixes.AnyAsync(p => p.Prefix == prefix, ct))
                return MasterDataSupport.InvalidReference("containerNo",
                    $"Prefix '{prefix}' is not registered to any party, and this tenant enforces prefixes.");
        }

        // Mirrors ck_container__lessor. A leased box with no lessor is a box nobody
        // can off-hire or invoice.
        if (ownershipType == "LEASED" && lessorPartyId is null)
            return MasterDataSupport.InvalidReference("lessorPartyId", "A LEASED container must name its lessor.");
        if (ownershipType != "LEASED" && lessorPartyId is not null)
            return MasterDataSupport.InvalidReference("lessorPartyId", $"Only a LEASED container has a lessor; this one is {ownershipType}.");

        if (equipmentTypeId is not null && !await db.EquipmentTypes.AnyAsync(t => t.EquipmentTypeId == equipmentTypeId, ct))
            return MasterDataSupport.InvalidReference("equipmentTypeId", "Unknown equipment type for this tenant.");

        if (ownerPartyId is not null && !await db.Parties.AnyAsync(p => p.PartyId == ownerPartyId, ct))
            return MasterDataSupport.InvalidReference("ownerPartyId", "Unknown party for this tenant.");

        if (lessorPartyId is not null && !await db.Parties.AnyAsync(p => p.PartyId == lessorPartyId, ct))
            return MasterDataSupport.InvalidReference("lessorPartyId", "Unknown party for this tenant.");

        if (isoCode is not null)
        {
            var code = isoCode.ToUpperInvariant();
            if (!await db.IsoContainerCodes.AnyAsync(i => i.IsoCode == code, ct))
                return MasterDataSupport.InvalidReference("isoCode", $"'{code}' is not an ISO 6346 size-type code.");

            // The box's ISO code has to be one its own type answers to, or the
            // outbound CODECO describes a different box than the registry does.
            if (equipmentTypeId is not null &&
                !await db.EquipmentTypeIsoCodes.AnyAsync(m => m.EquipmentTypeId == equipmentTypeId && m.IsoCode == code, ct))
                return MasterDataSupport.InvalidReference("isoCode",
                    $"'{code}' is not mapped to this equipment type. Map it first, or pick a code the type answers to.");
        }

        return null;
    }

    private static IQueryable<ContainerResponse> Project(MasterDataDbContext db, IQueryable<Container> containers) =>
        from c in containers
        join t in db.EquipmentTypes on c.EquipmentTypeId equals t.EquipmentTypeId into types
        from t in types.DefaultIfEmpty()
        join o in db.Parties on c.OwnerPartyId equals o.PartyId into owners
        from o in owners.DefaultIfEmpty()
        join l in db.Parties on c.LessorPartyId equals l.PartyId into lessors
        from l in lessors.DefaultIfEmpty()
        select new ContainerResponse(
            c.ContainerId, c.ContainerNo, c.ContainerNo.Substring(0, 4),
            c.EquipmentTypeId, t == null ? null : t.TypeCode, c.IsoCode,
            c.OwnerPartyId, o == null ? null : o.PartyCode,
            c.LessorPartyId, l == null ? null : l.PartyCode,
            c.OwnershipType, c.Material, c.ManufactureDate, c.Manufacturer,
            c.CscPlateRef, c.AcepRef, c.NextExaminationDate,
            c.TareWeightKg, c.MaxGrossKg, c.ReeferUnitMake, c.ReeferUnitModel,
            c.Status, c.StatusChangedAt, c.IsCheckDigitValid,
            c.CreatedAt, c.UpdatedAt, Convert.ToBase64String(c.RowVersion));
}
