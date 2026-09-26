using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class UsageCounter
{
    public Guid UsageId { get; set; }

    public Guid TenantId { get; set; }

    public Guid EntitlementId { get; set; }

    public string MetricCode { get; set; } = null!;

    public DateOnly PeriodStart { get; set; }

    public long UsedValue { get; set; }

    public long? LimitSnapshot { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; }
}
