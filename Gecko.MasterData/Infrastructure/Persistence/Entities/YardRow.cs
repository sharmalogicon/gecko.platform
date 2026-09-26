using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class YardRow
{
    public Guid YardRowId { get; set; }

    public Guid TenantId { get; set; }

    public Guid YardBlockId { get; set; }

    public string RowLabel { get; set; } = null!;

    public string? DescriptionEn { get; set; }

    public bool IsReeferRow { get; set; }

    public bool IsOogRow { get; set; }

    public short? ReeferPlugCount { get; set; }

    public bool IsBlocked { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
