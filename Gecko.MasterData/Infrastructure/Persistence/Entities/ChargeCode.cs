using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class ChargeCode
{
    public Guid ChargeCodeId { get; set; }

    public Guid TenantId { get; set; }

    public string ChargeCode1 { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string ModuleCode { get; set; } = null!;

    public string ChargeType { get; set; } = null!;

    public string ChargeCategory { get; set; } = null!;

    public string BillingUnitCode { get; set; } = null!;

    public bool IsByService { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
