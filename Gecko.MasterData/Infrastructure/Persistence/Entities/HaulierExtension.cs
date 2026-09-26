using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class HaulierExtension
{
    public Guid PartyId { get; set; }

    public Guid TenantId { get; set; }

    public short? FleetSize { get; set; }

    public byte? PrimaryChassisSizeFt { get; set; }

    public bool OwnsTrucks { get; set; }

    public string? TransportLicenceNo { get; set; }

    public Guid? DefaultRateCardRef { get; set; }

    public string? TruckingZoneCodes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
