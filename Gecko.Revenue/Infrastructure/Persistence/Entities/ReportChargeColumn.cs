namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

/// <summary>A tenant's charge code and the report column it falls in (gecko_revenue 31).</summary>
public partial class ReportChargeColumn
{
    public Guid TenantId { get; set; }

    public string ReportKey { get; set; } = null!;

    public string ChargeCode { get; set; } = null!;

    public string ColumnKey { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
