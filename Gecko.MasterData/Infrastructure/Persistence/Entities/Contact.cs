using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Contact
{
    public Guid ContactId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? PartyId { get; set; }

    public Guid? VesselId { get; set; }

    public Guid? PortId { get; set; }

    public Guid? BranchId { get; set; }

    public string ContactType { get; set; } = null!;

    public string? ContactPerson { get; set; }

    public string? JobTitle { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Postcode { get; set; }

    public string? CountryCode { get; set; }

    public string? Phone { get; set; }

    public string? Mobile { get; set; }

    public string? Email { get; set; }

    public string? LineUserId { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
