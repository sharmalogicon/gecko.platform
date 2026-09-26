using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class TemplateExport
{
    public Guid TemplateExportId { get; set; }

    public Guid TenantId { get; set; }

    public Guid Token { get; set; }

    public Guid ScheduleId { get; set; }

    public byte[] ScheduleRowVersion { get; set; } = null!;

    public string ModuleCode { get; set; } = null!;

    public string TemplateKind { get; set; } = null!;

    public short TemplateVersion { get; set; }

    public int RowCount { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    public Guid? GeneratedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
