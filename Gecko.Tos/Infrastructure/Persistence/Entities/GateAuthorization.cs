using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class GateAuthorization
{
    public Guid GateAuthorizationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid BookingId { get; set; }

    public string? ContainerNo { get; set; }

    public string MovementCode { get; set; } = null!;

    public string CouponRef { get; set; } = null!;

    public string PaymentChannel { get; set; } = null!;

    public decimal? Amount { get; set; }

    public string? CurrencyCode { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset ValidUntil { get; set; }

    public Guid? ConsumedByGateTransactionId { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? RevokeReason { get; set; }

    public Guid? SourceEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
