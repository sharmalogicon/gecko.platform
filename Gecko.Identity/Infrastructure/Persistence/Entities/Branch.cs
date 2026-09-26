using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Branch
{
    public Guid BranchId { get; set; }

    public Guid TenantId { get; set; }

    public string BranchCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string BranchType { get; set; } = null!;

    public string? Unlocode { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string CountryCode { get; set; } = null!;

    public string? Timezone { get; set; }

    public string? DefaultLocale { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
