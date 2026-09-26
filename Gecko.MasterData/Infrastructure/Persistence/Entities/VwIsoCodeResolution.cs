using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class VwIsoCodeResolution
{
    public string IsoCode { get; set; } = null!;

    public string Standard { get; set; } = null!;

    public string GroupCode { get; set; } = null!;

    public decimal LengthFt { get; set; }

    public int? HeightMm { get; set; }

    public bool IsReefer { get; set; }

    public bool IsHighCube { get; set; }

    public string? SupersededBy { get; set; }

    public string IsoDescription { get; set; } = null!;

    public Guid TenantId { get; set; }

    public Guid EquipmentTypeId { get; set; }

    public string TypeCode { get; set; } = null!;

    public bool IsDefaultOutbound { get; set; }
}
