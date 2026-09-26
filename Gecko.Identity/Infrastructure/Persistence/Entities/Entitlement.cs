using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Entitlement
{
    public Guid EntitlementId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    public string ModuleCode { get; set; } = null!;

    public Guid PlanId { get; set; }

    public string Status { get; set; } = null!;

    public string BillingCycle { get; set; } = null!;

    public DateTimeOffset? TrialEndsAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset? CurrentPeriodStart { get; set; }

    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    public bool CancelAtPeriodEnd { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public decimal? PriceOverride { get; set; }

    public string? CurrencyOverride { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
