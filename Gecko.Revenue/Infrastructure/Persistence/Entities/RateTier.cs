using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class RateTier
{
    public Guid RateTierId { get; set; }

    public Guid TenantId { get; set; }

    public string OwnerType { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public decimal FromQty { get; set; }

    public decimal? ToQty { get; set; }

    public decimal Rate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
