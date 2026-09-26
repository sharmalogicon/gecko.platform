using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class EquipmentType
{
    public Guid EquipmentTypeId { get; set; }

    public Guid TenantId { get; set; }

    public string TypeCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public decimal LengthFt { get; set; }

    public string HeightClass { get; set; } = null!;

    public string IsoGroupCode { get; set; } = null!;

    public decimal Teu { get; set; }

    public bool IsReefer { get; set; }

    public bool IsOog { get; set; }

    public bool IsTank { get; set; }

    public decimal? TareWeightKg { get; set; }

    public decimal? MaxPayloadKg { get; set; }

    public decimal? MaxGrossKg { get; set; }

    public string? DisplayColorHex { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
