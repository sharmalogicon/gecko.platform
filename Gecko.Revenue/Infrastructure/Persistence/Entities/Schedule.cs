using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Schedule
{
    public Guid ScheduleId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    public string ModuleCode { get; set; } = null!;

    public string ScheduleNo { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string ScheduleType { get; set; } = null!;

    public Guid LineageId { get; set; }

    public short VersionNo { get; set; }

    public Guid? SupersedesScheduleId { get; set; }

    public Guid? AgentPartyId { get; set; }

    public string? AgentPartyCode { get; set; }

    public Guid? ForwarderPartyId { get; set; }

    public string? ForwarderPartyCode { get; set; }

    public Guid? CustomerPartyId { get; set; }

    public string? CustomerPartyCode { get; set; }

    public string? BookingRef { get; set; }

    public byte? ScopeRank { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public bool PricesIncludeTax { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? RejectedAt { get; set; }

    public Guid? RejectedBy { get; set; }

    public string? RejectionReason { get; set; }

    public Guid? SalesUserId { get; set; }

    public bool WaiveDamagedEmptyStorage { get; set; }

    public string? LegacyQuotationNo { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
