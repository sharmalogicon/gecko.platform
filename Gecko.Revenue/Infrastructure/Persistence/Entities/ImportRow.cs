using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ImportRow
{
    public Guid ImportRowId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ImportBatchId { get; set; }

    public string SheetName { get; set; } = null!;

    public int RowNo { get; set; }

    public Guid? TargetId { get; set; }

    public string? Action { get; set; }

    public string Status { get; set; } = null!;

    public string RawJson { get; set; } = null!;

    public string? ResolvedJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
