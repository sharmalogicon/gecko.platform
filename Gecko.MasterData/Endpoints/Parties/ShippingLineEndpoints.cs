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

namespace Gecko.MasterData.Endpoints.Parties;

public sealed record ShippingLineResponse(
    string PartyCode, string NameEn, string? NameLocal, string? ShortName, bool IsActive,
    string LineRole, string? PrincipalLineCode, string? PrincipalLineName,
    string? ScacCode, string? SmdgCode, string? OperatorCode, string? ImoCompanyNo,
    string? AllianceCode, string? AllianceName, string? EdiPartnerCode,
    bool EdiSupportsCoparn, bool EdiSupportsCodeco, bool EdiSupportsCoarri, bool EdiSupportsBaplie,
    string? BrandColorHex, string? City, string CountryCode, DateTimeOffset UpdatedAt, string RowVersion);

public sealed record ShippingLineAgentResponse(string PartyCode, string NameEn, bool IsActive);

public sealed record ShippingLineDetailResponse(ShippingLineResponse Line, IReadOnlyList<ShippingLineAgentResponse> Agents);

/// <summary>
/// The line's own columns (party.shipping_line_extension). A PUT replaces all of
/// them — null clears — because the line editor always sends the whole form.
/// RowVersion is the EXTENSION's, not the party's: the name and address are
/// edited on the party screen and do not collide with this one.
/// </summary>
public sealed record SaveShippingLineRequest(
    [property: Required, AllowedValues("LINE", "AGENT")] string LineRole,
    [property: MaxLength(60)] string? PrincipalLineCode = null,
    [property: RegularExpression("^[A-Za-z]{4}$", ErrorMessage = "A SCAC is 4 letters.")] string? ScacCode = null,
    [property: MaxLength(10)] string? SmdgCode = null,
    [property: MaxLength(10)] string? OperatorCode = null,
    [property: MaxLength(15)] string? ImoCompanyNo = null,
    [property: MaxLength(20)] string? AllianceCode = null,
    [property: MaxLength(100)] string? AllianceName = null,
    [property: MaxLength(35)] string? EdiPartnerCode = null,
    bool EdiSupportsCoparn = false,
    bool EdiSupportsCodeco = false,
    bool EdiSupportsCoarri = false,
    bool EdiSupportsBaplie = false,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "A colour is #RRGGBB.")] string? BrandColorHex = null,
    string? RowVersion = null);

/// <summary>A new line: the party's name plus the line's columns. Address, tax id and contacts are added on the party screen.</summary>
public sealed record CreateShippingLineRequest(
    [property: Required, MaxLength(255)] string NameEn,
    [property: MaxLength(255)] string? NameLocal = null,
    [property: MaxLength(60)] string? ShortName = null,
    [property: Required, AllowedValues("LINE", "AGENT")] string LineRole = "LINE",
    [property: MaxLength(60)] string? PrincipalLineCode = null,
    [property: RegularExpression("^[A-Za-z]{4}$", ErrorMessage = "A SCAC is 4 letters.")] string? ScacCode = null,
    [property: MaxLength(10)] string? SmdgCode = null,
    [property: MaxLength(10)] string? OperatorCode = null,
    [property: MaxLength(15)] string? ImoCompanyNo = null,
    [property: MaxLength(20)] string? AllianceCode = null,
    [property: MaxLength(100)] string? AllianceName = null,
    [property: MaxLength(35)] string? EdiPartnerCode = null,
    bool EdiSupportsCoparn = false,
    bool EdiSupportsCodeco = false,
    bool EdiSupportsCoarri = false,
    bool EdiSupportsBaplie = false,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "A colour is #RRGGBB.")] string? BrandColorHex = null)
{
    public SaveShippingLineRequest Line() => new(
        LineRole, PrincipalLineCode, ScacCode, SmdgCode, OperatorCode, ImoCompanyNo, AllianceCode, AllianceName, EdiPartnerCode,
        EdiSupportsCoparn, EdiSupportsCodeco, EdiSupportsCoarri, EdiSupportsBaplie, BrandColorHex);
}

/// <summary>
/// Shipping lines — a view over parties that hold the SHIPPING_LINE role, with
/// the role's own columns (SCAC, SMDG, EDI flags, principal). There is no line
/// table: a line IS a party, so deactivating, deleting and editing the name or
/// address stay on /parties.
///
/// An AGENT acts for a principal LINE (KORAKIT: APL-N for APL,
/// 02_master_MAPPING §3.4). A principal with live agents cannot become an agent,
/// lose the line role or be deleted — the agents would point at nothing.
///
/// WHO: reading needs mdm.party.view (branch grant is enough, like parties).
/// Creating and editing a line need mdm.party.manage — not the counter's
/// mdm.party.create, which is for walk-in customers.
/// </summary>
internal static class ShippingLineEndpoints
{
    public static RouteGroupBuilder MapShippingLineEndpoints(this RouteGroupBuilder master)
    {
        var lines = master.MapGroup("/shipping-lines").WithTags("Master data — parties");

        lines.MapGet("/", ListAsync).RequireBranchPermission(MasterDataPermissions.PartyView).WithSummary("List shipping lines and agents with their SCAC, SMDG and EDI settings");
        lines.MapGet("/{partyCode}", GetAsync).RequireBranchPermission(MasterDataPermissions.PartyView).WithName("GetShippingLine").WithSummary("Get one shipping line with the agents that act for it");
        lines.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.PartyManage).Validate<CreateShippingLineRequest>().WithSummary("Register a shipping line (a party with the SHIPPING_LINE role); the code comes from the PARTY_CODE number series");
        lines.MapPut("/{partyCode}", UpdateAsync).RequirePermission(MasterDataPermissions.PartyManage).Validate<SaveShippingLineRequest>().WithSummary("Update a line's SCAC, SMDG, EDI and principal (optimistic concurrency on rowVersion)");

        return master;
    }

    /// <summary>Codes of the live agents acting for <paramref name="partyId"/>, for the 400/409 that protects them.</summary>
    internal static Task<List<string>> AgentCodesAsync(MasterDataDbContext db, Guid partyId, CancellationToken ct) =>
        (from e in db.ShippingLineExtensions.AsNoTracking()
         join p in db.Parties.AsNoTracking() on e.PartyId equals p.PartyId
         where e.PrincipalLinePartyId == partyId
         orderby p.PartyCode
         select p.PartyCode).ToListAsync(ct);

    internal static string PrincipalMessage(IReadOnlyList<string> agents) =>
        $"{string.Join(", ", agents.Take(5))}{(agents.Count > 5 ? $" and {agents.Count - 5} more" : "")} {(agents.Count == 1 ? "acts" : "act")} for this line. Point them at another line first.";

    /// <summary>An initialiser, not a positional record: EF can filter and sort on its members after the projection.</summary>
    private sealed class LineRow
    {
        public ShippingLineExtension E { get; init; } = null!;
        public Party P { get; init; } = null!;
        public string? PrincipalCode { get; init; }
        public string? PrincipalName { get; init; }
    }

    private static IQueryable<LineRow> Rows(MasterDataDbContext db, IQueryable<ShippingLineExtension> lines) =>
        from e in lines
        join p in db.Parties.AsNoTracking() on e.PartyId equals p.PartyId
        join pp in db.Parties.AsNoTracking() on e.PrincipalLinePartyId equals (Guid?)pp.PartyId into principals
        from pp in principals.DefaultIfEmpty()
        select new LineRow { E = e, P = p, PrincipalCode = pp == null ? null : pp.PartyCode, PrincipalName = pp == null ? null : pp.NameEn };

    private static ShippingLineResponse Map(LineRow r) => new(
        r.P.PartyCode, r.P.NameEn, r.P.NameLocal, r.P.ShortName, r.P.IsActive,
        r.E.LineRole, r.PrincipalCode, r.PrincipalName,
        r.E.ScacCode, r.E.SmdgCode, r.E.OperatorCode, r.E.ImoCompanyNo,
        r.E.AllianceCode, r.E.AllianceName, r.E.EdiPartnerCode,
        r.E.EdiSupportsCoparn, r.E.EdiSupportsCodeco, r.E.EdiSupportsCoarri, r.E.EdiSupportsBaplie,
        r.E.BrandColorHex, r.P.PrimaryCity, r.P.CountryCode, r.E.UpdatedAt, Convert.ToBase64String(r.E.RowVersion));

    private static async Task<Results<Ok<PagedResult<ShippingLineResponse>>, ValidationProblem>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, string? lineRole = null, int? page = 1, int? pageSize = 50, bool includeInactive = true)
    {
        var lines = db.ShippingLineExtensions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(lineRole))
        {
            var role = lineRole.Trim().ToUpperInvariant();
            if (role is not ("LINE" or "AGENT")) return MasterDataSupport.InvalidReference("lineRole", "Use LINE or AGENT.");
            lines = lines.Where(e => e.LineRole == role);
        }

        var rows = Rows(db, lines);
        if (!includeInactive) rows = rows.Where(r => r.P.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            rows = rows.Where(r =>
                r.P.PartyCode.Contains(s) || r.P.NameEn.Contains(s)
                || (r.P.NameLocal != null && r.P.NameLocal.Contains(s))
                || (r.P.ShortName != null && r.P.ShortName.Contains(s))
                || (r.E.ScacCode != null && r.E.ScacCode.Contains(s))
                || (r.E.SmdgCode != null && r.E.SmdgCode.Contains(s))
                || (r.PrincipalCode != null && r.PrincipalCode.Contains(s)));
        }

        // A line, then the agents that act for it.
        var ordered = rows
            .OrderBy(r => r.PrincipalName ?? r.P.NameEn).ThenBy(r => r.PrincipalCode ?? r.P.PartyCode)
            .ThenBy(r => r.E.LineRole == "AGENT").ThenBy(r => r.P.PartyCode);
        var paged = await ordered.ToPagedAsync(page, pageSize, ct);
        return TypedResults.Ok(new PagedResult<ShippingLineResponse>(
            paged.Items.Select(Map).ToList(), paged.Page, paged.PageSize, paged.TotalCount));
    }

    private static async Task<Results<Ok<ShippingLineDetailResponse>, NotFound>> GetAsync(
        string partyCode, MasterDataDbContext db, CancellationToken ct)
    {
        var detail = await DetailAsync(db, partyCode.FromRouteCode(), ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<ShippingLineDetailResponse?> DetailAsync(MasterDataDbContext db, string code, CancellationToken ct)
    {
        var row = await Rows(db, db.ShippingLineExtensions.AsNoTracking()).SingleOrDefaultAsync(r => r.P.PartyCode == code, ct);
        if (row is null) return null;

        var agents = await (
            from e in db.ShippingLineExtensions.AsNoTracking()
            join p in db.Parties.AsNoTracking() on e.PartyId equals p.PartyId
            where e.PrincipalLinePartyId == row.P.PartyId
            orderby p.PartyCode
            select new ShippingLineAgentResponse(p.PartyCode, p.NameEn, p.IsActive)).ToListAsync(ct);
        return new ShippingLineDetailResponse(Map(row), agents);
    }

    private static async Task<Results<CreatedAtRoute<ShippingLineDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateShippingLineRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var line = request.Line();
        var checkedLine = await CheckAsync(db, line, partyId: null, ct);
        if (checkedLine.Invalid is { } invalid) return invalid;

        var registered = await PartyEndpoints.RegisterAsync(db, caller,
            new SavePartyRequest(request.NameEn, NameLocal: request.NameLocal, ShortName: request.ShortName),
            ["SHIPPING_LINE"],
            party => Apply(db.ShippingLineExtensions.Local.Single(e => e.PartyId == party.PartyId), line, checkedLine.PrincipalPartyId),
            ct);
        if (registered.Invalid is { } registerInvalid) return registerInvalid;
        if (registered.Problem is { } problem) return problem;

        var code = registered.Party!.PartyCode;
        return TypedResults.CreatedAtRoute((await DetailAsync(db, code, ct))!, "GetShippingLine", new { partyCode = code });
    }

    private static async Task<Results<Ok<ShippingLineDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string partyCode, SaveShippingLineRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var code = partyCode.FromRouteCode();
        var partyId = await db.Parties.AsNoTracking().Where(p => p.PartyCode == code).Select(p => (Guid?)p.PartyId).SingleOrDefaultAsync(ct);
        var line = partyId is null ? null : await db.ShippingLineExtensions.SingleOrDefaultAsync(e => e.PartyId == partyId, ct);
        if (line is null) return TypedResults.NotFound();
        if (db.ExpectVersion(line, request.RowVersion) is { } missing) return missing;

        var checkedLine = await CheckAsync(db, request, partyId, ct);
        if (checkedLine.Invalid is { } invalid) return invalid;

        Apply(line, request, checkedLine.PrincipalPartyId);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok((await DetailAsync(db, code, ct))!);
    }

    private sealed record Checked(Guid? PrincipalPartyId, ValidationProblem? Invalid = null);

    /// <summary>The rules the attributes cannot express: principal, agents and a unique SCAC. All errors are returned at once.</summary>
    private static async Task<Checked> CheckAsync(MasterDataDbContext db, SaveShippingLineRequest r, Guid? partyId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        Guid? principalId = null;
        var principalCode = Clean(r.PrincipalLineCode)?.ToUpperInvariant();

        if (r.LineRole == "LINE" && principalCode is not null)
            errors["principalLineCode"] = ["Only an agent has a principal line."];
        else if (r.LineRole == "AGENT")
        {
            var principal = principalCode is null ? null : await (
                from p in db.Parties.AsNoTracking()
                join e in db.ShippingLineExtensions.AsNoTracking() on p.PartyId equals e.PartyId into roles
                from e in roles.DefaultIfEmpty()
                where p.PartyCode == principalCode
                select new { p.PartyId, LineRole = e == null ? null : e.LineRole }).SingleOrDefaultAsync(ct);

            if (principalCode is null) errors["principalLineCode"] = ["An agent needs the line it acts for."];
            else if (principal is null) errors["principalLineCode"] = [$"There is no party '{principalCode}'."];
            else if (principal.PartyId == partyId) errors["principalLineCode"] = ["An agent cannot act for itself."];
            else if (principal.LineRole is null) errors["principalLineCode"] = [$"{principalCode} is not a shipping line."];
            else if (principal.LineRole != "LINE") errors["principalLineCode"] = [$"{principalCode} is an agent; pick the line it acts for."];
            else principalId = principal.PartyId;

            if (partyId is { } id && await AgentCodesAsync(db, id, ct) is { Count: > 0 } agents)
                errors["lineRole"] = [PrincipalMessage(agents)];
        }

        if (Clean(r.ScacCode)?.ToUpperInvariant() is { } scac)
        {
            var holder = await (
                from e in db.ShippingLineExtensions.AsNoTracking()
                join p in db.Parties.AsNoTracking() on e.PartyId equals p.PartyId
                where e.ScacCode == scac && e.PartyId != partyId
                select p.PartyCode).FirstOrDefaultAsync(ct);
            if (holder is not null) errors["scacCode"] = [$"SCAC {scac} is already used by {holder}."];
        }

        return errors.Count == 0 ? new(principalId) : new(null, TypedResults.ValidationProblem(errors));
    }

    private static void Apply(ShippingLineExtension e, SaveShippingLineRequest r, Guid? principalPartyId)
    {
        e.LineRole = r.LineRole;
        e.PrincipalLinePartyId = principalPartyId;
        e.ScacCode = Clean(r.ScacCode)?.ToUpperInvariant();
        e.SmdgCode = Clean(r.SmdgCode)?.ToUpperInvariant();
        e.OperatorCode = Clean(r.OperatorCode)?.ToUpperInvariant();
        e.ImoCompanyNo = Clean(r.ImoCompanyNo);
        e.AllianceCode = Clean(r.AllianceCode)?.ToUpperInvariant();
        e.AllianceName = Clean(r.AllianceName);
        e.EdiPartnerCode = Clean(r.EdiPartnerCode);
        e.EdiSupportsCoparn = r.EdiSupportsCoparn;
        e.EdiSupportsCodeco = r.EdiSupportsCodeco;
        e.EdiSupportsCoarri = r.EdiSupportsCoarri;
        e.EdiSupportsBaplie = r.EdiSupportsBaplie;
        e.BrandColorHex = Clean(r.BrandColorHex)?.ToUpperInvariant();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
