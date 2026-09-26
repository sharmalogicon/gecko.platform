using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class EquipmentTypeIsoCode
{
    public Guid EquipmentTypeIsoCodeId { get; set; }

    public Guid TenantId { get; set; }

    public Guid EquipmentTypeId { get; set; }

    public string IsoCode { get; set; } = null!;

    public bool IsDefaultOutbound { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
