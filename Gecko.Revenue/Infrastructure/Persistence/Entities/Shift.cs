using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Shift
{
    public Guid ShiftId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid CashierUserId { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public DateTimeOffset OpenedAt { get; set; }

    public decimal OpeningFloat { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedBy { get; set; }

    public string? CloseNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
