using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class ContainerHold
{
    public Guid ContainerHoldId { get; set; }

    public Guid TenantId { get; set; }

    public string? ContainerNo { get; set; }

    public Guid? BookingId { get; set; }

    public Guid HoldId { get; set; }

    public string HoldCode { get; set; } = null!;

    public DateTimeOffset AppliedAt { get; set; }

    public Guid? AppliedBy { get; set; }

    public string ApplyReason { get; set; } = null!;

    public string? ExternalRef { get; set; }

    public string Source { get; set; } = null!;

    public DateTimeOffset? ReleasedAt { get; set; }

    public Guid? ReleasedBy { get; set; }

    public string? ReleaseReason { get; set; }

    public string? ReleaseRef { get; set; }

    public string? ReleaseSource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
