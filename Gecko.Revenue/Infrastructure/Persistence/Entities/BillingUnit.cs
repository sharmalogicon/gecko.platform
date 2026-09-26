using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class BillingUnit
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string QuantitySource { get; set; } = null!;

    public bool IsTimeBased { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset ReplicatedAt { get; set; }
}
