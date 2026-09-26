using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class YardBlock
{
    public Guid YardBlockId { get; set; }

    public Guid TenantId { get; set; }

    public Guid YardId { get; set; }

    public string BlockCode { get; set; } = null!;

    public byte? MaxRows { get; set; }

    public byte? MaxBays { get; set; }

    public byte? MaxTiers { get; set; }

    public Guid? AllocatedToPartyId { get; set; }

    public byte? AllocatedSizeFt { get; set; }

    public bool IsReeferBlock { get; set; }

    public bool IsDgBlock { get; set; }

    public bool IsOogBlock { get; set; }

    public int? LayoutX { get; set; }

    public int? LayoutY { get; set; }

    public int? LayoutWidth { get; set; }

    public int? LayoutHeight { get; set; }

    public short? LayoutRotationDeg { get; set; }

    public string? DisplayColorHex { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
