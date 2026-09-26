using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class TruckVisit
{
    public Guid TruckVisitId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string VisitNo { get; set; } = null!;

    public string? LaneCode { get; set; }

    public string TruckPlate { get; set; } = null!;

    public string? TrailerPlate { get; set; }

    public Guid? HaulierPartyId { get; set; }

    public string? HaulierPartyCode { get; set; }

    public string? DriverName { get; set; }

    public byte[]? DriverLicenceHash { get; set; }

    public Guid? SlotBookingId { get; set; }

    public DateTimeOffset ArrivedAt { get; set; }

    public DateTimeOffset? GateInAt { get; set; }

    public DateTimeOffset? GateOutAt { get; set; }

    public int? DwellMinutes { get; set; }

    public string Source { get; set; } = null!;

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
