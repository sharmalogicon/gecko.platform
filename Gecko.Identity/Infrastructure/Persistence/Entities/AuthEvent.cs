using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class AuthEvent
{
    public long AuthEventId { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public string? EmailAttempted { get; set; }

    public string EventType { get; set; } = null!;

    public string Outcome { get; set; } = null!;

    public string? FailureReason { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public Guid? CorrelationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
