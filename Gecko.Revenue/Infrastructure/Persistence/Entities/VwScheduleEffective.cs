using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class VwScheduleEffective
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

    public byte? ScopeRank { get; set; }

    public Guid? AgentPartyId { get; set; }

    public Guid? ForwarderPartyId { get; set; }

    public Guid? CustomerPartyId { get; set; }

    public string? BookingRef { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public bool PricesIncludeTax { get; set; }

    public bool WaiveDamagedEmptyStorage { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveUntil { get; set; }

    public bool? IsFullySuperseded { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
}
