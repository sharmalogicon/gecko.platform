using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ContainerStay
{
    public Guid ContainerStayId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public string? EquipmentTypeCode { get; set; }

    public string? IsoCode { get; set; }

    public string? LineCode { get; set; }

    public string FullEmptyIn { get; set; } = null!;

    public string? FullEmptyOut { get; set; }

    public Guid InGateTransactionId { get; set; }

    public string? InEirNo { get; set; }

    public Guid? InBookingId { get; set; }

    public DateTimeOffset InAt { get; set; }

    public Guid? OutGateTransactionId { get; set; }

    public string? OutEirNo { get; set; }

    public Guid? OutBookingId { get; set; }

    public DateTimeOffset? OutAt { get; set; }

    public string Status { get; set; } = null!;

    public string Source { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
