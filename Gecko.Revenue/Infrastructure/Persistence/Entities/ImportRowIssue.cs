using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ImportRowIssue
{
    public Guid ImportRowIssueId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ImportRowId { get; set; }

    public string? ColumnName { get; set; }

    public string Severity { get; set; } = null!;

    public string IssueCode { get; set; } = null!;

    public string Message { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
