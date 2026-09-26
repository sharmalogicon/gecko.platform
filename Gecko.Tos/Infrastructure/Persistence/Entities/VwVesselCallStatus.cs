using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwVesselCallStatus
{
    public Guid VesselCallId { get; set; }

    public Guid TenantId { get; set; }

    public string CallRef { get; set; } = null!;

    public string VesselCode { get; set; } = null!;

    public string PortCode { get; set; } = null!;

    public string? TerminalCode { get; set; }

    public DateTimeOffset Eta { get; set; }

    public DateTimeOffset? Etb { get; set; }

    public DateTimeOffset Etd { get; set; }

    public DateTimeOffset? Ata { get; set; }

    public DateTimeOffset? Atb { get; set; }

    public DateTimeOffset? Atd { get; set; }

    public DateTimeOffset? LastYardCutoffAt { get; set; }

    public string CallStatus { get; set; } = null!;
}
