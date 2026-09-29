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

public sealed record SaveContactRequest(
    [property: Required, AllowedValues("BILLING", "OPERATIONS", "SHIPPING", "GATE", "CUSTOMS", "EMERGENCY", "TECHNICAL", "OTHER")] string ContactType,
    [property: MaxLength(200)] string? ContactPerson = null,
    [property: MaxLength(100)] string? JobTitle = null,
    [property: MaxLength(50)] string? Phone = null,
    [property: MaxLength(50)] string? Mobile = null,
    [property: MaxLength(255), RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "Not an e-mail address.")] string? Email = null,
    [property: MaxLength(255)] string? Address1 = null,
    [property: MaxLength(255)] string? Address2 = null,
    [property: MaxLength(100)] string? City = null,
    [property: MaxLength(100)] string? State = null,
    [property: MaxLength(25)] string? Postcode = null,
    bool IsDefault = false,
    string? RowVersion = null);

/// <summary>
/// A party's contacts — for KORAKIT mostly tax-invoice addresses (Vector's
/// Master.Contact was an address book: 13 of 12,302 rows name a person).
///
/// Editing a contact is editing the party's data, so it needs the same
/// mdm.party.manage as PUT /parties/{code}. One DEFAULT per contact type
/// (uq_contact__default_party): making one the default clears the previous one in
/// the same transaction instead of failing on the index.
/// </summary>
internal static class ContactEndpoints
{
    public static RouteGroupBuilder MapContactEndpoints(this RouteGroupBuilder master)
    {
        var contacts = master.MapGroup("/parties/{partyCode}/contacts").WithTags("Master data — parties");
        contacts.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.PartyManage).Validate<SaveContactRequest>().WithSummary("Add a contact to a party");
        contacts.MapPut("/{contactId:guid}", UpdateAsync).RequirePermission(MasterDataPermissions.PartyManage).Validate<SaveContactRequest>().WithSummary("Update a contact");
        contacts.MapDelete("/{contactId:guid}", DeleteAsync).RequirePermission(MasterDataPermissions.PartyManage).WithSummary("Soft-delete a contact");
        return master;
    }

    internal static PartyContactResponse Map(Contact c) => new(
        c.ContactId, c.ContactPerson, c.ContactType, c.JobTitle, c.Phone, c.Mobile, c.Email, c.IsDefault,
        c.Address1, c.Address2, c.City, c.State, c.Postcode, Convert.ToBase64String(c.RowVersion));

    private static async Task<Results<Created<PartyContactResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string partyCode, SaveContactRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var partyId = await PartyIdAsync(db, partyCode, ct);
        if (partyId is null) return TypedResults.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.IsDefault && await ClearDefaultAsync(db, partyId.Value, request.ContactType, except: null, ct) is { } conflict) return conflict;

        var contact = new Contact { TenantId = caller.TenantId(), PartyId = partyId, IsActive = true };
        Apply(contact, request);
        db.Contacts.Add(contact);
        if (await db.SaveOrConflictAsync(ct) is { } saveConflict) return saveConflict;
        await tx.CommitAsync(ct);

        return TypedResults.Created($"/api/master/parties/{Uri.EscapeDataString(partyCode)}/contacts/{contact.ContactId}", Map(contact));
    }

    private static async Task<Results<Ok<PartyContactResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string partyCode, Guid contactId, SaveContactRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var contact = await FindAsync(db, partyCode, contactId, ct);
        if (contact is null) return TypedResults.NotFound();
        if (db.ExpectVersion(contact, request.RowVersion) is { } missing) return missing;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.IsDefault && await ClearDefaultAsync(db, contact.PartyId!.Value, request.ContactType, except: contactId, ct) is { } conflict) return conflict;

        Apply(contact, request);
        if (await db.SaveOrConflictAsync(ct) is { } saveConflict) return saveConflict;
        await tx.CommitAsync(ct);
        return TypedResults.Ok(Map(contact));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string partyCode, Guid contactId, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var contact = await FindAsync(db, partyCode, contactId, ct);
        if (contact is null) return TypedResults.NotFound();
        if (db.ExpectVersion(contact, rowVersion) is { } missing) return missing;
        db.Contacts.Remove(contact);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static Task<Guid?> PartyIdAsync(MasterDataDbContext db, string partyCode, CancellationToken ct) =>
        db.Parties.AsNoTracking().Where(p => p.PartyCode == partyCode.FromRouteCode()).Select(p => (Guid?)p.PartyId).SingleOrDefaultAsync(ct);

    /// <summary>The contact, only if it belongs to the party named in the route (and so to this tenant).</summary>
    private static async Task<Contact?> FindAsync(MasterDataDbContext db, string partyCode, Guid contactId, CancellationToken ct) =>
        await PartyIdAsync(db, partyCode, ct) is { } partyId
            ? await db.Contacts.SingleOrDefaultAsync(c => c.ContactId == contactId && c.PartyId == partyId, ct)
            : null;

    /// <summary>Un-defaults the other default of this type and flushes it, so the filtered unique index sees one default at a time.</summary>
    private static async Task<ProblemHttpResult?> ClearDefaultAsync(MasterDataDbContext db, Guid partyId, string contactType, Guid? except, CancellationToken ct)
    {
        var previous = await db.Contacts.Where(c => c.PartyId == partyId && c.ContactType == contactType && c.IsDefault && c.ContactId != except).ToListAsync(ct);
        if (previous.Count == 0) return null;
        foreach (var p in previous) p.IsDefault = false;
        return await db.SaveOrConflictAsync(ct);
    }

    private static void Apply(Contact c, SaveContactRequest r)
    {
        c.ContactType = r.ContactType;
        c.ContactPerson = Trim(r.ContactPerson);
        c.JobTitle = Trim(r.JobTitle);
        c.Phone = Trim(r.Phone);
        c.Mobile = Trim(r.Mobile);
        c.Email = Trim(r.Email);
        c.Address1 = Trim(r.Address1);
        c.Address2 = Trim(r.Address2);
        c.City = Trim(r.City);
        c.State = Trim(r.State);
        c.Postcode = Trim(r.Postcode);
        c.IsDefault = r.IsDefault;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
