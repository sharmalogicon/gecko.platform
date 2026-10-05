using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class BoxReservation
{
    public Guid BoxReservationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid DraftId { get; set; }

    public Guid? BookingContainerId { get; set; }

    public string? ContainerNo { get; set; }

    public Guid ReservedBy { get; set; }

    public DateTimeOffset ReservedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public Guid? ReleasedBy { get; set; }

    public string? ReleaseReason { get; set; }

    public Guid? GateTransactionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
