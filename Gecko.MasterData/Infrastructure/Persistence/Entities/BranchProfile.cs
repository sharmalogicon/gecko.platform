using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class BranchProfile
{
    public Guid BranchId { get; set; }

    public Guid TenantId { get; set; }

    public string BranchCode { get; set; } = null!;

    public Guid? CompanyId { get; set; }

    public string? NameLocal { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Postcode { get; set; }

    public string? CountryCode { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public string? Timezone { get; set; }

    public string? UnLocode { get; set; }

    public string? CustomsOfficeCode { get; set; }

    public string? TaxBranchCode { get; set; }

    public string? EdiLocationCode { get; set; }

    public DateTimeOffset? SyncedFromIdentityAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
