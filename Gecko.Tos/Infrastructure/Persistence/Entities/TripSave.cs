using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class TripSave
{
    public Guid TripSaveId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid DraftId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public byte[] IdempotencyHash { get; set; } = null!;

    public string Status { get; set; } = null!;

    public Guid? TruckVisitId { get; set; }

    public Guid? ReceiptId { get; set; }

    public string? ReceiptNo { get; set; }

    public string? ResultJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
