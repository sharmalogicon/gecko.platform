using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class BookingPlan
{
    public Guid BookingId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string OrderNo { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string OrderTypeCode { get; set; } = null!;

    public string? BookingTypeCode { get; set; }

    public string? DirectionCode { get; set; }

    public string? LineCode { get; set; }

    public string? AgentPartyCode { get; set; }

    public string? CustomerPartyCode { get; set; }

    public string? ForwarderPartyCode { get; set; }

    public string? HaulierPartyCode { get; set; }

    public string? CargoClassCode { get; set; }

    public string? CargoCategoryCode { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    public string? RequirementsJson { get; set; }

    public long LastMessageId { get; set; }

    public string LastReason { get; set; } = null!;

    public DateTimeOffset ChangedAt { get; set; }

    public string PayloadJson { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
