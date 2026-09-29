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

public sealed record PartySummaryResponse(
    Guid PartyId, string PartyCode, string NameEn, string? NameLocal, string? TaxId, string? BranchNo,
    bool IsActive, IReadOnlyList<string> Roles);

public sealed record PartyAliasResponse(string Code, string Type, string? Label);

public sealed record PartyContactResponse(
    Guid ContactId, string? Name, string Role, string? JobTitle, string? Phone, string? Mobile, string? Email, bool IsDefault,
    string? Address1, string? Address2, string? City, string? State, string? Postcode, string RowVersion);

/// <summary>Another live party with the same tax id and tax branch — a dedupe hint, never merged automatically.</summary>
public sealed record PartyDuplicateResponse(string PartyCode, string NameEn, bool IsActive);

public sealed record PartyDetailResponse(
    Guid PartyId, string PartyCode, string NameEn, string? NameLocal, string? TaxId, string? BranchNo,
    bool IsActive, IReadOnlyList<string> Roles,
    string? ShortName, string CountryCode, string? DefaultCurrency,
    string? Address, string? Address2, string? City, string? State, string? Postcode,
    string? Phone, string? Email, string? Website, string? Remarks,
    IReadOnlyList<PartyAliasResponse> Aliases,
    IReadOnlyList<PartyContactResponse> Contacts,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion,
    string? RegistrationNo = null,
    IReadOnlyList<PartyDuplicateResponse>? Duplicates = null);

/// <summary>
/// The contract fields are NameEn … Roles. Address2 / City / State / Postcode /
/// ShortName / Website / Remarks / RegistrationNo / DefaultCurrency are optional
/// extras: on PUT a null extra means "leave as it is" (send "" to clear), so a
/// client that only knows the contract fields cannot wipe them by accident.
/// DefaultCurrency is an ISO 4217 code from lookup.currency; on POST it defaults
/// to the tenant company's currency, else THB.
/// </summary>
public sealed record SavePartyRequest(
    [property: Required, MaxLength(255)] string NameEn,
    [property: MaxLength(255)] string? NameLocal = null,
    [property: MaxLength(30)] string? TaxId = null,
    [property: MaxLength(10)] string? BranchNo = null,
    [property: MaxLength(255)] string? Address = null,
    [property: MaxLength(50)] string? Phone = null,
    [property: MaxLength(255), RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "Not an e-mail address.")] string? Email = null,
    IReadOnlyList<string>? Roles = null,
    [property: MaxLength(255)] string? Address2 = null,
    [property: MaxLength(100)] string? City = null,
    [property: MaxLength(100)] string? State = null,
    [property: MaxLength(25)] string? Postcode = null,
    [property: MaxLength(60)] string? ShortName = null,
    bool? IsActive = null,
    string? RowVersion = null,
    [property: MaxLength(500)] string? Website = null,
    [property: MaxLength(1000)] string? Remarks = null,
    [property: MaxLength(100)] string? RegistrationNo = null,
    [property: RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "A currency is a 3-letter ISO 4217 code.")] string? DefaultCurrency = null);

/// <summary>
/// Parties — customers, shipping lines, forwarders, hauliers — one row in
/// party.party, one ROLE per extension table.
///
/// WHO: search, read and REGISTER open to a branch grant (<c>bpm</c>) as well as a
/// tenant-wide one, because KORAKIT's counter staff are branch-scoped
/// GATE_CLERKs who must find — or register — a walk-in customer before issuing
/// the receipt. The customer register itself is tenant-wide (one customer, many
/// depots), so there is no branch filter on the rows. Registering uses its own
/// permission, mdm.party.create (gecko_identity 19): mdm.party.manage also edits
/// credit terms, which the counter must not. EDITING stays tenant-wide
/// (RequirePermission) — changing a customer changes it at every depot.
/// </summary>
internal static class PartyEndpoints
{
    public const string PartyCreate = "mdm.party.create";

    private static readonly string[] AllRoles = ["CUSTOMER", "SHIPPING_LINE", "FORWARDER", "HAULIER"];

    public static RouteGroupBuilder MapPartyEndpoints(this RouteGroupBuilder master)
    {
        var parties = master.MapGroup("/parties").WithTags("Master data — parties");

        parties.MapGet("/", ListAsync).RequireBranchPermission(MasterDataPermissions.PartyView).WithSummary("Search parties by code, name (English or Thai), tax id or alias");
        parties.MapGet("/{partyCode}", GetAsync).RequireBranchPermission(MasterDataPermissions.PartyView).WithName("GetParty").WithSummary("Get one party with its aliases and contacts");
        parties.MapPost("/", CreateAsync).RequireBranchPermission(PartyCreate).Validate<SavePartyRequest>().WithSummary("Register a party; the code comes from the PARTY_CODE number series");
        parties.MapPut("/{partyCode}", UpdateAsync).RequirePermission(MasterDataPermissions.PartyManage).Validate<SavePartyRequest>().WithSummary("Update a party (optimistic concurrency on rowVersion)");
        parties.MapDelete("/{partyCode}", DeleteAsync).RequirePermission(MasterDataPermissions.PartyManage).WithSummary("Soft-delete a party with its roles, aliases and contacts");

        return master;
    }

    private sealed record PartyRow(
        Guid PartyId, string PartyCode, string NameEn, string? NameLocal, string? TaxId, string? BranchNo, bool IsActive,
        bool IsCustomer, bool IsLine, bool IsForwarder, bool IsHaulier);

    private static IQueryable<PartyRow> Rows(MasterDataDbContext db, IQueryable<Party> parties) =>
        parties.Select(p => new PartyRow(
            p.PartyId, p.PartyCode, p.NameEn, p.NameLocal, p.TaxId, p.TaxBranchCode, p.IsActive,
            db.CustomerExtensions.Any(e => e.PartyId == p.PartyId),
            db.ShippingLineExtensions.Any(e => e.PartyId == p.PartyId),
            db.ForwarderExtensions.Any(e => e.PartyId == p.PartyId),
            db.HaulierExtensions.Any(e => e.PartyId == p.PartyId)));

    private static IReadOnlyList<string> RolesOf(PartyRow r)
    {
        var roles = new List<string>(4);
        if (r.IsCustomer) roles.Add("CUSTOMER");
        if (r.IsLine) roles.Add("SHIPPING_LINE");
        if (r.IsForwarder) roles.Add("FORWARDER");
        if (r.IsHaulier) roles.Add("HAULIER");
        return roles;
    }

    private static PartySummaryResponse ToSummary(PartyRow r) =>
        new(r.PartyId, r.PartyCode, r.NameEn, r.NameLocal, r.TaxId, r.BranchNo, r.IsActive, RolesOf(r));

    private static async Task<Results<Ok<PagedResult<PartySummaryResponse>>, ValidationProblem>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, string? role = null, int? page = 1, int? pageSize = 20, bool includeInactive = true)
    {
        var parties = db.Parties.AsNoTracking();
        if (!includeInactive) parties = parties.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Collation is case-insensitive, so Contains is the case-insensitive match
            // the contract asks for — including Thai, which has no case.
            var s = search.Trim();
            // Alias hits are found ONCE, then matched by id. As a correlated EXISTS inside
            // the OR, SQL Server re-scanned every alias for every party — 15-16 s per search
            // over KORAKIT's 9,540 parties / 9,543 aliases (measured 2026-09-29).
            var aliasHits = await db.PartyAliases.AsNoTracking()
                .Where(a => a.AliasValue.Contains(s)).Select(a => a.PartyId).Distinct().Take(500).ToListAsync(ct);
            parties = parties.Where(p =>
                p.PartyCode.Contains(s) || p.NameEn.Contains(s) || (p.NameLocal != null && p.NameLocal.Contains(s))
                || (p.ShortName != null && p.ShortName.Contains(s))
                || (p.TaxId != null && p.TaxId.Contains(s))
                || aliasHits.Contains(p.PartyId));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            parties = role.Trim().ToUpperInvariant() switch
            {
                "CUSTOMER" => parties.Where(p => db.CustomerExtensions.Any(e => e.PartyId == p.PartyId)),
                "SHIPPING_LINE" => parties.Where(p => db.ShippingLineExtensions.Any(e => e.PartyId == p.PartyId)),
                "FORWARDER" => parties.Where(p => db.ForwarderExtensions.Any(e => e.PartyId == p.PartyId)),
                "HAULIER" => parties.Where(p => db.HaulierExtensions.Any(e => e.PartyId == p.PartyId)),
                _ => null!,
            };
            if (parties is null)
                return MasterDataSupport.InvalidReference("role", $"Unknown role. Use one of: {string.Join(", ", AllRoles)}.");
        }

        var paged = await Rows(db, parties.OrderBy(p => p.NameEn).ThenBy(p => p.PartyCode)).ToPagedAsync(page, pageSize, ct);
        return TypedResults.Ok(new PagedResult<PartySummaryResponse>(
            paged.Items.Select(ToSummary).ToList(), paged.Page, paged.PageSize, paged.TotalCount));
    }

    private static async Task<Results<Ok<PartyDetailResponse>, NotFound>> GetAsync(
        string partyCode, MasterDataDbContext db, CancellationToken ct)
    {
        var code = partyCode.FromRouteCode();
        var party = await db.Parties.AsNoTracking().SingleOrDefaultAsync(p => p.PartyCode == code, ct);
        return party is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, party, ct));
    }

    private static async Task<PartyDetailResponse> DetailAsync(MasterDataDbContext db, Party p, CancellationToken ct)
    {
        var row = await Rows(db, db.Parties.AsNoTracking().Where(x => x.PartyId == p.PartyId)).SingleAsync(ct);

        var aliases = await db.PartyAliases.AsNoTracking()
            .Where(a => a.PartyId == p.PartyId)
            .OrderBy(a => a.AliasType).ThenBy(a => a.AliasValue)
            .Select(a => new PartyAliasResponse(a.AliasValue, a.AliasType, a.AliasLabel))
            .ToListAsync(ct);

        var contacts = (await db.Contacts.AsNoTracking()
            .Where(c => c.PartyId == p.PartyId && c.IsActive)
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.ContactType).ThenBy(c => c.ContactPerson)
            .ToListAsync(ct)).Select(ContactEndpoints.Map).ToList();

        // KORAKIT has 1,100 (tax id, branch) groups used in parallel (02_master_MAPPING §3.3):
        // shown so a clerk picks the right code, never merged here.
        var duplicates = p.TaxId is null ? [] : await db.Parties.AsNoTracking()
            .Where(x => x.TaxId == p.TaxId && x.TaxBranchCode == p.TaxBranchCode && x.PartyId != p.PartyId)
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.PartyCode).Take(20)
            .Select(x => new PartyDuplicateResponse(x.PartyCode, x.NameEn, x.IsActive))
            .ToListAsync(ct);

        return new PartyDetailResponse(
            p.PartyId, p.PartyCode, p.NameEn, p.NameLocal, p.TaxId, p.TaxBranchCode, p.IsActive, RolesOf(row),
            p.ShortName, p.CountryCode, p.DefaultCurrency,
            p.PrimaryAddress1, p.PrimaryAddress2, p.PrimaryCity, p.PrimaryState, p.PrimaryPostcode,
            p.PrimaryPhone, p.PrimaryEmail, p.PrimaryWebsite, p.Remarks,
            aliases, contacts, p.CreatedAt, p.UpdatedAt, Convert.ToBase64String(p.RowVersion), p.RegistrationNo, duplicates);
    }

    private static async Task<Results<CreatedAtRoute<PartyDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SavePartyRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking()
            .OrderBy(c => c.DefaultCurrency == null).ThenBy(c => c.CompanyCode)
            .Select(c => new { c.CountryCode, c.DefaultCurrency })
            .FirstOrDefaultAsync(ct);
        var countryCode = company?.CountryCode ?? "TH";

        var roles = NormaliseRoles(request.Roles, out var badRole);
        if (badRole is not null) return MasterDataSupport.InvalidReference("roles", badRole);
        if (roles.Count == 0) roles = ["CUSTOMER"];

        var taxId = Clean(request.TaxId);
        var branchNo = Clean(request.BranchNo);
        if (ValidateTaxId(taxId, countryCode) is { } taxProblem) return taxProblem;
        if (await DuplicateAsync(db, taxId, branchNo, exceptPartyId: null, ct) is { } duplicate) return duplicate;

        // A party with no currency cannot be quoted or invoiced: default it to the
        // tenant's own (org.company), else THB.
        var currency = Clean(request.DefaultCurrency)?.ToUpperInvariant() ?? Clean(company?.DefaultCurrency) ?? "THB";
        if (await CurrencyProblemAsync(db, currency, ct) is { } currencyProblem) return currencyProblem;

        // party_id is NEWSEQUENTIALID() in the database, so it only exists after the
        // party row is inserted; the role rows are keyed on it. Party first, roles
        // second, one transaction — and the number is drawn inside it too.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var code = await NextPartyCodeAsync(db, ct);
        if (code is null)
            return MasterDataSupport.InvalidReference("partyCode", "No active PARTY_CODE number series for this tenant.");

        var party = new Party
        {
            TenantId = caller.TenantId(),
            PartyCode = code,
            CountryCode = countryCode,
            IsActive = true,   // a new party is active; deactivating is an edit
        };
        Apply(party, request, taxId, branchNo, isCreate: true);
        party.DefaultCurrency = currency;
        db.Parties.Add(party);
        await db.SaveChangesAsync(ct);
        if (party.PartyId == Guid.Empty) throw new InvalidOperationException("party_id was not read back after insert.");

        await SetRolesAsync(db, party, roles, caller.TenantId(), removeOthers: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var saved = await db.Parties.AsNoTracking().SingleAsync(p => p.PartyId == party.PartyId, ct);
        return TypedResults.CreatedAtRoute(await DetailAsync(db, saved, ct), "GetParty", new { partyCode = code });
    }

    private static async Task<Results<Ok<PartyDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string partyCode, SavePartyRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var party = await db.Parties.SingleOrDefaultAsync(p => p.PartyCode == partyCode.FromRouteCode(), ct);
        if (party is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(party, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        var roles = NormaliseRoles(request.Roles, out var badRole);
        if (badRole is not null) return MasterDataSupport.InvalidReference("roles", badRole);

        var taxId = Clean(request.TaxId);
        var branchNo = Clean(request.BranchNo);
        // Only a CHANGED tax id is format-checked: migrated rows may carry legacy
        // values, and an unrelated edit (a phone number) must still save.
        if (!string.Equals(taxId, party.TaxId, StringComparison.Ordinal) && ValidateTaxId(taxId, party.CountryCode) is { } taxProblem)
            return taxProblem;
        if ((!string.Equals(taxId, party.TaxId, StringComparison.Ordinal) || !string.Equals(branchNo, party.TaxBranchCode, StringComparison.Ordinal))
            && await DuplicateAsync(db, taxId, branchNo, party.PartyId, ct) is { } duplicate)
            return duplicate;

        if (request.DefaultCurrency is not null)
        {
            var currency = Clean(request.DefaultCurrency)?.ToUpperInvariant();
            if (currency is not null && !string.Equals(currency, party.DefaultCurrency, StringComparison.Ordinal)
                && await CurrencyProblemAsync(db, currency, ct) is { } currencyProblem)
                return currencyProblem;
            party.DefaultCurrency = currency;
        }

        Apply(party, request, taxId, branchNo, isCreate: false);
        if (request.IsActive is { } active) party.IsActive = active;
        if (roles.Count > 0) await SetRolesAsync(db, party, roles, caller.TenantId(), removeOthers: true, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var saved = await db.Parties.AsNoTracking().SingleAsync(p => p.PartyId == party.PartyId, ct);
        return TypedResults.Ok(await DetailAsync(db, saved, ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string partyCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var party = await db.Parties.SingleOrDefaultAsync(p => p.PartyCode == partyCode.FromRouteCode(), ct);
        if (party is null) return TypedResults.NotFound();
        if (db.ExpectVersion(party, rowVersion) is { } missing) return missing;

        db.CustomerExtensions.RemoveRange(await db.CustomerExtensions.Where(e => e.PartyId == party.PartyId).ToListAsync(ct));
        db.ShippingLineExtensions.RemoveRange(await db.ShippingLineExtensions.Where(e => e.PartyId == party.PartyId).ToListAsync(ct));
        db.ForwarderExtensions.RemoveRange(await db.ForwarderExtensions.Where(e => e.PartyId == party.PartyId).ToListAsync(ct));
        db.HaulierExtensions.RemoveRange(await db.HaulierExtensions.Where(e => e.PartyId == party.PartyId).ToListAsync(ct));
        db.PartyAliases.RemoveRange(await db.PartyAliases.Where(a => a.PartyId == party.PartyId).ToListAsync(ct));
        db.Contacts.RemoveRange(await db.Contacts.Where(c => c.PartyId == party.PartyId).ToListAsync(ct));
        db.Parties.Remove(party);   // soft delete: AuditStampInterceptor turns it into deleted_at
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>null extra on PUT = unchanged; "" = clear.</summary>
    private static string? Extra(string? requested, string? current, bool isCreate) =>
        requested is null ? (isCreate ? null : current) : Clean(requested);

    private static void Apply(Party party, SavePartyRequest r, string? taxId, string? branchNo, bool isCreate)
    {
        party.NameEn = r.NameEn.Trim();
        party.NameLocal = Clean(r.NameLocal);
        party.TaxId = taxId;
        party.TaxBranchCode = branchNo;
        party.PrimaryAddress1 = Clean(r.Address);
        party.PrimaryPhone = Clean(r.Phone);
        party.PrimaryEmail = Clean(r.Email);
        party.PrimaryAddress2 = Extra(r.Address2, party.PrimaryAddress2, isCreate);
        party.PrimaryCity = Extra(r.City, party.PrimaryCity, isCreate);
        party.PrimaryState = Extra(r.State, party.PrimaryState, isCreate);
        party.PrimaryPostcode = Extra(r.Postcode, party.PrimaryPostcode, isCreate);
        party.ShortName = Extra(r.ShortName, party.ShortName, isCreate);
        party.PrimaryWebsite = Extra(r.Website, party.PrimaryWebsite, isCreate);
        party.Remarks = Extra(r.Remarks, party.Remarks, isCreate);
        party.RegistrationNo = Extra(r.RegistrationNo, party.RegistrationNo, isCreate);
    }

    private static async Task<ValidationProblem?> CurrencyProblemAsync(MasterDataDbContext db, string? currency, CancellationToken ct) =>
        currency is null || await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == currency && c.IsActive, ct)
            ? null
            : MasterDataSupport.InvalidReference("defaultCurrency", $"Unknown or inactive currency '{currency}'.");

    private static List<string> NormaliseRoles(IReadOnlyList<string>? roles, out string? problem)
    {
        problem = null;
        var result = (roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToUpperInvariant()).Distinct().ToList();
        var unknown = result.Except(AllRoles).ToList();
        if (unknown.Count > 0) problem = $"Unknown role(s) {string.Join(", ", unknown)}. Use: {string.Join(", ", AllRoles)}.";
        return result;
    }

    /// <summary>A Thai tax id (เลขประจำตัวผู้เสียภาษี) is exactly 13 digits.</summary>
    private static ValidationProblem? ValidateTaxId(string? taxId, string countryCode)
    {
        if (taxId is null || countryCode != "TH") return null;
        return taxId.Length == 13 && taxId.All(char.IsAsciiDigit)
            ? null
            : MasterDataSupport.InvalidReference("taxId", "A Thai tax id is exactly 13 digits.");
    }

    /// <summary>
    /// Same tax id AND same tax branch = the same legal entity at the same
    /// branch: that is a duplicate, and the caller is told which code to use.
    /// A different branch of the same company is a legitimately separate party.
    /// </summary>
    private static async Task<ProblemHttpResult?> DuplicateAsync(
        MasterDataDbContext db, string? taxId, string? branchNo, Guid? exceptPartyId, CancellationToken ct)
    {
        if (taxId is null) return null;
        var existing = await db.Parties.AsNoTracking()
            .Where(p => p.TaxId == taxId && p.TaxBranchCode == branchNo && p.PartyId != exceptPartyId)
            .Select(p => p.PartyCode)
            .FirstOrDefaultAsync(ct);
        if (existing is null) return null;

        return TypedResults.Problem(
            title: "Duplicate tax id and branch.",
            detail: $"A party with this tax id and branch already exists: {existing}. Use it instead of registering it again.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["existingPartyCode"] = existing });
    }

    private static async Task<string?> NextPartyCodeAsync(MasterDataDbContext db, CancellationToken ct)
    {
        var number = new Microsoft.Data.SqlClient.SqlParameter
        {
            ParameterName = "@number",
            SqlDbType = System.Data.SqlDbType.NVarChar,
            Size = 60,
            Direction = System.Data.ParameterDirection.Output,
        };
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "EXEC config.usp_next_number @series_key = @key, @number = @number OUTPUT",
                [new Microsoft.Data.SqlClient.SqlParameter("@key", "PARTY_CODE"), number], ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 50020 or 50021)
        {
            return null;
        }
        return number.Value as string;
    }

    /// <summary>
    /// One extension row per role. Extension tables are keyed on party_id alone,
    /// so a role that was removed earlier is RESTORED (its soft-deleted row
    /// brought back with its credit terms), never inserted twice.
    /// </summary>
    private static async Task SetRolesAsync(
        MasterDataDbContext db, Party party, IReadOnlyCollection<string> roles, Guid tenantId, bool removeOthers, CancellationToken ct)
    {
        await SetRoleAsync(db.CustomerExtensions, party.PartyId, roles.Contains("CUSTOMER"), removeOthers,
            () => new CustomerExtension { PartyId = party.PartyId, TenantId = tenantId, IsBilling = true, TierCode = "STANDARD", DefaultPaymentTerm = "CASH", IsVatRegistered = true },
            e => e.PartyId, e => e.DeletedAt, (e, v) => { e.DeletedAt = v; e.DeletedBy = null; }, ct);
        await SetRoleAsync(db.ShippingLineExtensions, party.PartyId, roles.Contains("SHIPPING_LINE"), removeOthers,
            () => new ShippingLineExtension { PartyId = party.PartyId, TenantId = tenantId, LineRole = "LINE" },
            e => e.PartyId, e => e.DeletedAt, (e, v) => { e.DeletedAt = v; e.DeletedBy = null; }, ct);
        await SetRoleAsync(db.ForwarderExtensions, party.PartyId, roles.Contains("FORWARDER"), removeOthers,
            () => new ForwarderExtension { PartyId = party.PartyId, TenantId = tenantId },
            e => e.PartyId, e => e.DeletedAt, (e, v) => { e.DeletedAt = v; e.DeletedBy = null; }, ct);
        await SetRoleAsync(db.HaulierExtensions, party.PartyId, roles.Contains("HAULIER"), removeOthers,
            () => new HaulierExtension { PartyId = party.PartyId, TenantId = tenantId },
            e => e.PartyId, e => e.DeletedAt, (e, v) => { e.DeletedAt = v; e.DeletedBy = null; }, ct);
    }

    private static async Task SetRoleAsync<T>(
        DbSet<T> set, Guid partyId, bool wanted, bool removeOthers, Func<T> create,
        System.Linq.Expressions.Expression<Func<T, Guid>> key, Func<T, DateTimeOffset?> deletedAt,
        Action<T, DateTimeOffset?> restore, CancellationToken ct) where T : class
    {
        var predicate = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.Equal(key.Body, System.Linq.Expressions.Expression.Constant(partyId)),
            key.Parameters);
        var row = await set.IgnoreQueryFilters().Where(predicate).SingleOrDefaultAsync(ct);

        if (wanted)
        {
            if (row is null) set.Add(create());
            else if (deletedAt(row) is not null) restore(row, null);
        }
        else if (removeOthers && row is not null && deletedAt(row) is null)
        {
            set.Remove(row);   // soft delete
        }
    }
}
