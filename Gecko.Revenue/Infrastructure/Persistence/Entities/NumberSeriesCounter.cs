using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class NumberSeriesCounter
{
    public Guid NumberSeriesId { get; set; }

    public string BranchCode { get; set; } = null!;

    public string PeriodKey { get; set; } = null!;

    public Guid TenantId { get; set; }

    public long LastNumber { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
