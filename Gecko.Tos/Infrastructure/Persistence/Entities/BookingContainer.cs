using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class BookingContainer
{
    public Guid BookingContainerId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingId { get; set; }

    public Guid EquipmentRequirementId { get; set; }

    /// <summary>Null = a booked box not nominated yet (gecko_tos 22).</summary>
    public string? ContainerNo { get; set; }

    public Guid? ContainerId { get; set; }

    public bool IsCheckDigitValid { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public Guid? AssignedBy { get; set; }

    public string AssignmentSource { get; set; } = null!;

    public string? DeclaredSealNo { get; set; }

    public decimal? DeclaredVgmKg { get; set; }

    /// <summary>gecko_tos 18: Vector's P/U Mode / D/O Mode / repo mode — who collects or delivers the box. Null = not said.</summary>
    public string? HandoverModeCode { get; set; }

    /// <summary>gecko_tos 20: the UI's own id for the row it keyed — a retry with it gets the same line.</summary>
    public Guid? ClientLineId { get; set; }

    public string? CustomerSealNo { get; set; }

    public decimal? DeclaredVolumeCbm { get; set; }

    public DateOnly? RequiredDate { get; set; }

    public string? CargoCategoryCode { get; set; }

    public string? ImdgClass { get; set; }

    public string? UnNumber { get; set; }

    public decimal? ReeferSetTempC { get; set; }

    public decimal? ReeferVentPct { get; set; }

    public decimal? ReeferHumidityPct { get; set; }

    public string? StowageCode { get; set; }

    public string? StowageNo { get; set; }

    public bool? IsPreCool { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid? EndedBy { get; set; }

    public string? EndReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
