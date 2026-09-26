using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class VwRateTierDefect
{
    public Guid TenantId { get; set; }

    public Guid ScheduleId { get; set; }

    public Guid TosRateId { get; set; }

    public string ChargeCode { get; set; } = null!;

    public string Defect { get; set; } = null!;

    public decimal? FromQty { get; set; }

    public decimal? ToQty { get; set; }
}
