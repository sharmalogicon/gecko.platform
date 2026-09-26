using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Port
{
    public Guid PortId { get; set; }

    public Guid TenantId { get; set; }

    public string PortCode { get; set; } = null!;

    public string? UnLocode { get; set; }

    public string PortNameEn { get; set; } = null!;

    public string? PortNameLocal { get; set; }

    public string PortType { get; set; } = null!;

    public string CountryCode { get; set; } = null!;

    public string TradeMode { get; set; } = null!;

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public string? Timezone { get; set; }

    public string? Postcode { get; set; }

    public string? EdiMappingCode { get; set; }

    public string? PaperlessCode { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
