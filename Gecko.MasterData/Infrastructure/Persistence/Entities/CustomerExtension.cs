using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CustomerExtension
{
    public Guid PartyId { get; set; }

    public Guid TenantId { get; set; }

    public bool IsBilling { get; set; }

    public bool IsShipper { get; set; }

    public bool IsConsignee { get; set; }

    public string TierCode { get; set; } = null!;

    public string? DebtorCode { get; set; }

    public string? RevenueAccount { get; set; }

    public string DefaultPaymentTerm { get; set; } = null!;

    public short? CreditTermDays { get; set; }

    public decimal? CreditLimit { get; set; }

    public string? CreditLimitCurrency { get; set; }

    public short? LongStandingThresholdDays { get; set; }

    public bool IsVatRegistered { get; set; }

    public string? WithholdingTaxCode { get; set; }

    public Guid? DefaultTariffRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
