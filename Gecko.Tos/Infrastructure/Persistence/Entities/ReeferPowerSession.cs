using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class ReeferPowerSession
{
    public Guid ReeferPowerSessionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid ContainerVisitId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public DateTimeOffset PluggedInAt { get; set; }

    public Guid? PluggedInBy { get; set; }

    public DateTimeOffset? PluggedOutAt { get; set; }

    public Guid? PluggedOutBy { get; set; }

    public string? CloseReason { get; set; }

    public Guid? CloseGateTransactionId { get; set; }

    public string? PlugPointCode { get; set; }

    public decimal? SetPointC { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
