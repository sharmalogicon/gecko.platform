using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Vessel
{
    public Guid VesselId { get; set; }

    public Guid TenantId { get; set; }

    public string VesselCode { get; set; } = null!;

    public string VesselName { get; set; } = null!;

    public string? VesselNameLocal { get; set; }

    public string? ImoNumber { get; set; }

    public string? CallSign { get; set; }

    public string? Mmsi { get; set; }

    public string? VesselType { get; set; }

    public Guid? OperatorPartyId { get; set; }

    public string? FlagCountryCode { get; set; }

    public int? TeuCapacity { get; set; }

    public int? GrossTonnage { get; set; }

    public decimal? LoaM { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
