using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class OrderType
{
    public Guid OrderTypeId { get; set; }

    public Guid TenantId { get; set; }

    public string OrderTypeCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string DirectionCode { get; set; } = null!;

    public Guid? ServiceTypeId { get; set; }

    public string CargoClassCode { get; set; } = null!;

    public string? BookingTypeCode { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
