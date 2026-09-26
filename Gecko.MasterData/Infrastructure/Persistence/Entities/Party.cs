using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Party
{
    public Guid PartyId { get; set; }

    public Guid TenantId { get; set; }

    public string PartyCode { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameLocal { get; set; }

    public string? ShortName { get; set; }

    public string? RegistrationNo { get; set; }

    public string? TaxId { get; set; }

    public string? TaxBranchCode { get; set; }

    public string CountryCode { get; set; } = null!;

    public string? DefaultCurrency { get; set; }

    public string? PrimaryAddress1 { get; set; }

    public string? PrimaryAddress2 { get; set; }

    public string? PrimaryCity { get; set; }

    public string? PrimaryState { get; set; }

    public string? PrimaryPostcode { get; set; }

    public string? PrimaryPhone { get; set; }

    public string? PrimaryEmail { get; set; }

    public string? PrimaryWebsite { get; set; }

    public string? Remarks { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
