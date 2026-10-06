using System;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

/// <summary>gecko_tos 27: a box a truck came to collect — PLANNED at gate in, RELEASED by the gate-out EIR, or CANCELLED.</summary>
public partial class VisitPickup
{
    public Guid VisitPickupId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TruckVisitId { get; set; }
    public Guid BookingContainerId { get; set; }
    public string? ContainerNo { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset PlannedAt { get; set; }
    public Guid PlannedBy { get; set; }
    public Guid? TripSaveId { get; set; }
    public Guid? GateTransactionId { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = null!;
}
