using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwBookingDefect
{
    public Guid TenantId { get; set; }

    public Guid? BookingId { get; set; }

    public Guid SubjectId { get; set; }

    public string DefectCode { get; set; } = null!;

    public string Detail { get; set; } = null!;
}
