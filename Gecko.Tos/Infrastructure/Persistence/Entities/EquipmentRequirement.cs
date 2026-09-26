using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class EquipmentRequirement
{
    public Guid EquipmentRequirementId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingId { get; set; }

    public short LineNo { get; set; }

    public Guid EquipmentTypeId { get; set; }

    public string EquipmentTypeCode { get; set; } = null!;

    public short Qty { get; set; }

    public string? MinGradeCode { get; set; }

    public decimal? ReeferSetTempC { get; set; }

    public decimal? ReeferVentPct { get; set; }

    public decimal? ReeferHumidityPct { get; set; }

    public string? ImdgClass { get; set; }

    public string? UnNumber { get; set; }

    public short? OogOverHeightCm { get; set; }

    public short? OogOverWidthLeftCm { get; set; }

    public short? OogOverWidthRightCm { get; set; }

    public short? OogOverLengthFrontCm { get; set; }

    public short? OogOverLengthBackCm { get; set; }

    public decimal? DeclaredGrossWeightKg { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
