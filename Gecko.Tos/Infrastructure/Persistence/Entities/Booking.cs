using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class Booking
{
    public Guid BookingId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string OrderNo { get; set; } = null!;

    public string? CarrierRef { get; set; }

    /// <summary>gecko_tos 19: the Idempotency-Key the creating request carried; unique per tenant.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>gecko_tos 19: SHA-256 of that request — the same key with another body is refused.</summary>
    public byte[]? IdempotencyHash { get; set; }

    public Guid OrderTypeId { get; set; }

    public string OrderTypeCode { get; set; } = null!;

    public string BookingTypeCode { get; set; } = null!;

    public string DirectionCode { get; set; } = null!;

    public Guid LinePartyId { get; set; }

    public string LinePartyCode { get; set; } = null!;

    public Guid? AgentPartyId { get; set; }

    public string? AgentPartyCode { get; set; }

    public Guid? CustomerPartyId { get; set; }

    public string? CustomerPartyCode { get; set; }

    public Guid? ForwarderPartyId { get; set; }

    public string? ForwarderPartyCode { get; set; }

    public Guid? HaulierPartyId { get; set; }

    public string? HaulierPartyCode { get; set; }

    public Guid? VesselCallId { get; set; }

    public Guid? VesselCallLineId { get; set; }

    public Guid? PolPortId { get; set; }

    public string? PolPortCode { get; set; }

    public Guid? PodPortId { get; set; }

    public string? PodPortCode { get; set; }

    public Guid? FpdPortId { get; set; }

    public string? FpdPortCode { get; set; }

    public string CargoClassCode { get; set; } = null!;

    public string? CargoCategoryCode { get; set; }

    public Guid? CommodityId { get; set; }

    public string? CommodityCode { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedBy { get; set; }

    public string? CloseReason { get; set; }

    public string Source { get; set; } = null!;

    public string? EdiMessageRef { get; set; }

    public string? CustomerRef { get; set; }

    public string? Remarks { get; set; }

    public string? LegacyOrderNo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
