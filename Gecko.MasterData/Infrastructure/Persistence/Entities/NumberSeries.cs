using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class NumberSeries
{
    public Guid NumberSeriesId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    public string SeriesKey { get; set; } = null!;

    public string? DocumentTypeCode { get; set; }

    public string? Description { get; set; }

    public string? Prefix { get; set; }

    public string Separator { get; set; } = null!;

    public bool IncludeBranchCode { get; set; }

    public string DatePartFormat { get; set; } = null!;

    public string ResetPeriod { get; set; } = null!;

    public byte NumberLength { get; set; }

    public long StartNumber { get; set; }

    public bool IsGapFreeRequired { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
