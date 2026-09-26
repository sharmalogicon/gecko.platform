using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class PlanLimit
{
    public Guid PlanLimitId { get; set; }

    public Guid PlanId { get; set; }

    public string MetricCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public long? LimitValue { get; set; }

    public string Period { get; set; } = null!;

    public bool IsHardLimit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
