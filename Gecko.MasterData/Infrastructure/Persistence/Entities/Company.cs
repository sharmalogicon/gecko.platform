using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Company
{
    public Guid CompanyId { get; set; }

    public Guid TenantId { get; set; }

    public string CompanyCode { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameLocal { get; set; }

    public string? RegistrationNo { get; set; }

    public string? TaxId { get; set; }

    public string? TaxBranchCode { get; set; }

    public string? DefaultCurrency { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Postcode { get; set; }

    public string CountryCode { get; set; } = null!;

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public string? ShortName { get; set; }
}
