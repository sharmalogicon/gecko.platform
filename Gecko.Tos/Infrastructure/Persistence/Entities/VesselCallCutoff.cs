using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VesselCallCutoff
{
    public Guid VesselCallCutoffId { get; set; }

    public Guid TenantId { get; set; }

    public Guid VesselCallId { get; set; }

    public string CutoffKind { get; set; } = null!;

    public Guid? LinePartyId { get; set; }

    public string? LinePartyCode { get; set; }

    public Guid? BranchId { get; set; }

    public DateTimeOffset CutoffAt { get; set; }

    public string Source { get; set; } = null!;

    public short? DerivedLeadHours { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
