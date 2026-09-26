using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class RepairCode
{
    public Guid RepairCodeId { get; set; }

    public Guid TenantId { get; set; }

    public string RepairCode1 { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string CodeStandard { get; set; } = null!;

    public string? RepairMode { get; set; }

    public string? RepairGroup { get; set; }

    public string? DefaultUomCode { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
