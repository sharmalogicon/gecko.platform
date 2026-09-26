using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwVesselCallDefect
{
    public Guid TenantId { get; set; }

    public Guid VesselCallId { get; set; }

    public Guid SubjectId { get; set; }

    public string DefectCode { get; set; } = null!;

    public string? Detail { get; set; }
}
