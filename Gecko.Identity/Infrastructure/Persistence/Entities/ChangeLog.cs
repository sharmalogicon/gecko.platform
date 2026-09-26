using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class ChangeLog
{
    public long ChangeLogId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? ActorDescription { get; set; }

    public string EntityType { get; set; } = null!;

    public Guid? EntityId { get; set; }

    public string Action { get; set; } = null!;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public string? Reason { get; set; }

    public string? IpAddress { get; set; }

    public Guid? CorrelationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
