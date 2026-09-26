using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Hold
{
    public Guid HoldId { get; set; }

    public Guid TenantId { get; set; }

    public string HoldCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string HoldType { get; set; } = null!;

    public string BlockingScope { get; set; } = null!;

    public string ReleaseAuthority { get; set; } = null!;

    public byte Priority { get; set; }

    public string? DisplayColorHex { get; set; }

    public string? AutoApplyOnEvent { get; set; }

    public bool NotifyOnApply { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
