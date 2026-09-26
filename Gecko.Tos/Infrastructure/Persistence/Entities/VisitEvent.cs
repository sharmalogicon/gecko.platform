using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VisitEvent
{
    public long VisitEventId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContainerVisitId { get; set; }

    public string EventType { get; set; } = null!;

    public string? FromValue { get; set; }

    public string? ToValue { get; set; }

    public DateTimeOffset EventAt { get; set; }

    public Guid? EventBy { get; set; }

    public Guid? ReferenceId { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
