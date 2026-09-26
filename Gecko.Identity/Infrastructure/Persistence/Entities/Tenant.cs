using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Tenant
{
    public Guid TenantId { get; set; }

    public string TenantCode { get; set; } = null!;

    public string LegalName { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string CountryCode { get; set; } = null!;

    public string? TaxId { get; set; }

    public string Timezone { get; set; } = null!;

    public string DefaultLocale { get; set; } = null!;

    public string DefaultCurrency { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset? SuspendedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
