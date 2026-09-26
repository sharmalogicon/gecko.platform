using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class RateCondition
{
    public Guid RateConditionId { get; set; }

    public Guid TenantId { get; set; }

    public string OwnerType { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public short SequenceNo { get; set; }

    public string Axis { get; set; } = null!;

    public string Op { get; set; } = null!;

    public decimal? NumberValue { get; set; }

    public bool? BoolValue { get; set; }

    public string ModifierOp { get; set; } = null!;

    public decimal ModifierValue { get; set; }

    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
