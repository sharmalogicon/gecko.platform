using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class InvoiceLine
{
    public Guid InvoiceLineId { get; set; }

    public Guid TenantId { get; set; }

    public Guid InvoiceId { get; set; }

    public Guid? EntitlementId { get; set; }

    public string Description { get; set; } = null!;

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
