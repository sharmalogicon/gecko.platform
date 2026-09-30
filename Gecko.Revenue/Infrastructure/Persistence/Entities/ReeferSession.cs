using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ReeferSession
{
    public Guid SessionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContainerVisitId { get; set; }

    public Guid? InGateTransactionId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public Guid BranchId { get; set; }

    public string? EquipmentTypeCode { get; set; }

    public DateTimeOffset PluggedInAt { get; set; }

    public Guid? PluggedInBy { get; set; }

    public DateTimeOffset? PluggedOutAt { get; set; }

    public Guid? PluggedOutBy { get; set; }

    public string? CloseReason { get; set; }

    public Guid? CloseGateTransactionId { get; set; }

    public bool IsVoided { get; set; }

    public long LastMessageId { get; set; }

    public string LastMessageType { get; set; } = null!;

    public DateTimeOffset LastChangedAt { get; set; }

    public string PayloadJson { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
