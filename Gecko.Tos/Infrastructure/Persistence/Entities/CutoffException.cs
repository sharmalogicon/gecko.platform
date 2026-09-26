using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class CutoffException
{
    public Guid CutoffExceptionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingId { get; set; }

    public Guid? EquipmentRequirementId { get; set; }

    public string CutoffKind { get; set; } = null!;

    public DateTimeOffset AllowedUntil { get; set; }

    public DateTimeOffset ApprovedAt { get; set; }

    public Guid ApprovedBy { get; set; }

    public string Reason { get; set; } = null!;

    public string? CarrierApprovalRef { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? RevokeReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
