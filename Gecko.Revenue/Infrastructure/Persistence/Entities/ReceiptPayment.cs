using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ReceiptPayment
{
    public Guid ReceiptPaymentId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ReceiptId { get; set; }

    public string Channel { get; set; } = null!;

    public decimal Amount { get; set; }

    public decimal? TenderedAmount { get; set; }

    public decimal? ChangeAmount { get; set; }

    public string? ReferenceNo { get; set; }

    public string? BankName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
