using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Yard
{
    public Guid YardId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string YardCode { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameLocal { get; set; }

    public string YardType { get; set; } = null!;

    public string FullEmpty { get; set; } = null!;

    public string? DirectionCode { get; set; }

    public int? CapacityTeu { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
