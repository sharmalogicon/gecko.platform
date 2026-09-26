using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class SealRange
{
    public Guid SealRangeId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid PartyId { get; set; }

    public string SealPrefix { get; set; } = null!;

    public long SeriesStart { get; set; }

    public long SeriesEnd { get; set; }

    public byte? NumberLength { get; set; }

    public long? LastIssuedNumber { get; set; }

    public DateOnly? ReceivedOn { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
