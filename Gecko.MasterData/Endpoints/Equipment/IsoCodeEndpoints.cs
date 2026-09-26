using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Equipment;

public sealed record IsoContainerCodeResponse(
    string IsoCode, string Standard, string GroupCode, decimal? LengthFt, int? HeightMm, decimal? Teu,
    bool IsReefer, bool IsHighCube, bool IsOpenTop, bool IsPlatform, bool IsTank,
    string DescriptionEn, string? SupersededBy, string? MappingNote, bool IsActive);

public sealed record IsoTypeGroupResponse(
    string GroupCode, string DescriptionEn, bool IsReefer, bool IsOpenTop, bool IsPlatform, bool IsTank,
    bool IsDangerousCapable, string? DefaultCargoClass);

/// <summary>
/// The ISO 6346 size-type standard, READ ONLY and shared by every tenant.
///
/// These rows live in the `lookup` schema, which both SQL logins are DENIED
/// write on (01_security_foundation.sql) — so there is deliberately no POST,
/// PUT or DELETE here. A tenant that disagrees with the standard does not edit
/// the standard; it maps the code to its own equipment type, or adds a partner
/// code mapping. That is what Vector got wrong by storing '22G0;22G1;2200' in a
/// semicolon list on the local type and then editing it per customer.
/// </summary>
internal static class IsoCodeEndpoints
{
    public static RouteGroupBuilder MapIsoCodeEndpoints(this RouteGroupBuilder master)
    {
        var iso = master.MapGroup("/iso-codes").WithTags("Master data — ISO 6346 reference");

        iso.MapGet("/", ListAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("Browse the global ISO 6346 size-type codes");

        iso.MapGet("/groups", GroupsAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("List the ISO type groups (GP, RT, UT, TN...)");

        iso.MapGet("/{isoCode}", GetAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("Get one ISO 6346 size-type code");

        return master;
    }

    private static async Task<Ok<PagedResult<IsoContainerCodeResponse>>> ListAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct,
        string? groupCode = null, decimal? lengthFt = null, bool? isReefer = null, bool includeSuperseded = false)
    {
        var codes = db.IsoContainerCodes.AsNoTracking().Where(i => i.IsActive);

        // A superseded 1984 code (2200 -> 22G0) is valid to RECEIVE and wrong to
        // send, so it is hidden by default rather than deleted.
        if (!includeSuperseded) codes = codes.Where(i => i.SupersededBy == null);
        if (!string.IsNullOrWhiteSpace(groupCode)) codes = codes.Where(i => i.GroupCode == groupCode.ToUpperInvariant());
        if (lengthFt is not null) codes = codes.Where(i => i.LengthFt == lengthFt);
        if (isReefer is not null) codes = codes.Where(i => i.IsReefer == isReefer);
        if (!string.IsNullOrWhiteSpace(query.Search))
            codes = codes.Where(i => i.IsoCode.Contains(query.Search) || i.DescriptionEn.Contains(query.Search));

        return TypedResults.Ok(await codes.OrderBy(i => i.IsoCode).Select(ToResponse).ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<IsoContainerCodeResponse>, NotFound>> GetAsync(
        string isoCode, MasterDataDbContext db, CancellationToken ct) =>
        await db.IsoContainerCodes.AsNoTracking()
            .Where(i => i.IsoCode == isoCode.Trim().ToUpperInvariant())
            .Select(ToResponse)
            .SingleOrDefaultAsync(ct) is { } code
            ? TypedResults.Ok(code)
            : TypedResults.NotFound();

    private static async Task<Ok<IReadOnlyList<IsoTypeGroupResponse>>> GroupsAsync(MasterDataDbContext db, CancellationToken ct) =>
        TypedResults.Ok<IReadOnlyList<IsoTypeGroupResponse>>(await db.IsoTypeGroups.AsNoTracking()
            .OrderBy(g => g.GroupCode)
            .Select(g => new IsoTypeGroupResponse(g.GroupCode, g.DescriptionEn, g.IsReefer, g.IsOpenTop, g.IsPlatform, g.IsTank, g.IsDangerousCapable, g.DefaultCargoClass))
            .ToListAsync(ct));

    private static readonly Expression<Func<IsoContainerCode, IsoContainerCodeResponse>> ToResponse = i => new IsoContainerCodeResponse(
        i.IsoCode, i.Standard, i.GroupCode, i.LengthFt, i.HeightMm, i.Teu,
        i.IsReefer, i.IsHighCube, i.IsOpenTop, i.IsPlatform, i.IsTank,
        i.DescriptionEn, i.SupersededBy, i.MappingNote, i.IsActive);
}
