using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VesselCallLine
{
    public Guid VesselCallLineId { get; set; }

    public Guid TenantId { get; set; }

    public Guid VesselCallId { get; set; }

    public Guid LinePartyId { get; set; }

    public string LinePartyCode { get; set; } = null!;

    public Guid? AgentPartyId { get; set; }

    public string? AgentPartyCode { get; set; }

    public string? VoyageIn { get; set; }

    public string? VoyageOut { get; set; }

    public string? ServiceCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
