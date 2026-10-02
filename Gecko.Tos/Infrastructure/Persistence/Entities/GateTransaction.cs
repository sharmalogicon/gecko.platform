using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class GateTransaction
{
    public Guid GateTransactionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string EirNo { get; set; } = null!;

    public Guid TruckVisitId { get; set; }

    public string Direction { get; set; } = null!;

    public byte PositionNo { get; set; }

    public Guid MovementId { get; set; }

    public string MovementCode { get; set; } = null!;

    public string FullEmpty { get; set; } = null!;

    public string ContainerNo { get; set; } = null!;

    public Guid? ContainerId { get; set; }

    public bool IsCheckDigitValid { get; set; }

    public Guid? CheckDigitOverrideBy { get; set; }

    public string? CheckDigitOverrideReason { get; set; }

    public Guid? EquipmentTypeId { get; set; }

    public string? EquipmentTypeCode { get; set; }

    public string? IsoCode { get; set; }

    public Guid BookingId { get; set; }

    public Guid BookingContainerId { get; set; }

    public Guid MovementPlanId { get; set; }

    public Guid LinePartyId { get; set; }

    public string LinePartyCode { get; set; } = null!;

    public Guid? VesselCallId { get; set; }

    public decimal? GrossWeightKg { get; set; }

    public decimal? TareWeightKg { get; set; }

    public decimal? VgmKg { get; set; }

    public string? VgmMethod { get; set; }

    public string? WeightSource { get; set; }

    public string? ConditionCode { get; set; }

    public string? GradeCode { get; set; }

    public Guid? SurveyId { get; set; }

    public bool SealMismatch { get; set; }

    public decimal? TempObservedC { get; set; }

    public Guid? YardId { get; set; }

    public Guid? YardSlotId { get; set; }

    public string? PositionText { get; set; }

    public string? CutoffKindApplied { get; set; }

    public DateTimeOffset? CutoffAtApplied { get; set; }

    public bool IsLate { get; set; }

    public Guid? CutoffExceptionId { get; set; }

    public Guid? LateOverrideBy { get; set; }

    public string? LateOverrideReason { get; set; }

    public Guid? GateAuthorizationId { get; set; }

    public string? PriceSnapshotJson { get; set; }

    public DateTimeOffset TransactionAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    public string? VoidReason { get; set; }

    public Guid? ReplacesGateTransactionId { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public string? TripTypeCode { get; set; }

    public string? MaterialCode { get; set; }

    public decimal? MaxGrossWeightKg { get; set; }

    public decimal? CargoWeightKg { get; set; }

    public string? VentSetting { get; set; }

    public decimal? HumidityPct { get; set; }

    public string? GensetNo { get; set; }

    public string? ClipOnNo { get; set; }

    public string? CustomsPermitNo { get; set; }

    public string? PaperlessCode { get; set; }

    public string? NextLocationCode { get; set; }
}
