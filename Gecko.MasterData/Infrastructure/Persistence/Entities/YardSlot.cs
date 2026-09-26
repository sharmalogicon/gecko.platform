using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class YardSlot
{
    public Guid YardSlotId { get; set; }

    public Guid TenantId { get; set; }

    public Guid YardBlockId { get; set; }

    public string RowLabel { get; set; } = null!;

    public short BayNumber { get; set; }

    public short TierNumber { get; set; }

    public bool HasReeferPlug { get; set; }

    public string? ReeferPlugRef { get; set; }

    public Guid? ReservedForPartyId { get; set; }

    public bool IsBlocked { get; set; }

    public string? BlockReason { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
