using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwActiveHold
{
    public Guid TenantId { get; set; }

    public string? ContainerNo { get; set; }

    public Guid ContainerHoldId { get; set; }

    public Guid HoldId { get; set; }

    public string HoldCode { get; set; } = null!;

    public Guid? BookingId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    public string ApplyReason { get; set; } = null!;

    public string Source { get; set; } = null!;

    public string HeldVia { get; set; } = null!;
}
