using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ImportBatch
{
    public Guid ImportBatchId { get; set; }

    public Guid TenantId { get; set; }

    public Guid TemplateExportId { get; set; }

    public Guid ScheduleId { get; set; }

    public string TemplateKind { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public byte[] FileSha256 { get; set; } = null!;

    public int FileSizeBytes { get; set; }

    public string Status { get; set; } = null!;

    public int RowsTotal { get; set; }

    public int RowsOk { get; set; }

    public int RowsWarning { get; set; }

    public int RowsError { get; set; }

    public int RowsInsert { get; set; }

    public int RowsUpdate { get; set; }

    public int RowsDelete { get; set; }

    public int RowsUnchanged { get; set; }

    public bool ScheduleChangedSinceExport { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public Guid? UploadedBy { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public Guid? ConfirmedBy { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public string? FailureMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public byte[]? ScheduleRowVersion { get; set; }
}
