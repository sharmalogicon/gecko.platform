using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VesselCall
{
    public Guid VesselCallId { get; set; }

    public Guid TenantId { get; set; }

    public string CallRef { get; set; } = null!;

    public Guid VesselId { get; set; }

    public string VesselCode { get; set; } = null!;

    public Guid PortId { get; set; }

    public string PortCode { get; set; } = null!;

    public Guid? TerminalLocationId { get; set; }

    public string? TerminalCode { get; set; }

    public string? OperatorVoyageIn { get; set; }

    public string? OperatorVoyageOut { get; set; }

    public DateTimeOffset Eta { get; set; }

    public DateTimeOffset? Etb { get; set; }

    public DateTimeOffset Etd { get; set; }

    public DateTimeOffset? Ata { get; set; }

    public DateTimeOffset? Atb { get; set; }

    public DateTimeOffset? Atd { get; set; }

    public bool IsCancelled { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public string Source { get; set; } = null!;

    public string? LegacyVesselScheduleIds { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
