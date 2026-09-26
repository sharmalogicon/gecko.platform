using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ShiftCount
{
    public Guid ShiftCountId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ShiftId { get; set; }

    public string Channel { get; set; } = null!;

    public decimal ExpectedAmount { get; set; }

    public decimal CountedAmount { get; set; }

    public decimal? Variance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
