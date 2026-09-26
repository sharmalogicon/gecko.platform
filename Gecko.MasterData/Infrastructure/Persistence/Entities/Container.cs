using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Container
{
    public Guid ContainerId { get; set; }

    public Guid TenantId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public Guid? EquipmentTypeId { get; set; }

    public string? IsoCode { get; set; }

    public Guid? OwnerPartyId { get; set; }

    public Guid? LessorPartyId { get; set; }

    public string OwnershipType { get; set; } = null!;

    public string? Material { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    public string? Manufacturer { get; set; }

    public string? CscPlateRef { get; set; }

    public string? AcepRef { get; set; }

    public DateOnly? NextExaminationDate { get; set; }

    public decimal? TareWeightKg { get; set; }

    public decimal? MaxGrossKg { get; set; }

    public string? ReeferUnitMake { get; set; }

    public string? ReeferUnitModel { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? StatusChangedAt { get; set; }

    public bool IsCheckDigitValid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
