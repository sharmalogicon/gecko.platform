using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Plan
{
    public Guid PlanId { get; set; }

    public string ModuleCode { get; set; } = null!;

    public string PlanCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public int TierLevel { get; set; }

    public decimal PriceMonthly { get; set; }

    public decimal PriceYearly { get; set; }

    public string Currency { get; set; } = null!;

    public bool IsPublic { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
