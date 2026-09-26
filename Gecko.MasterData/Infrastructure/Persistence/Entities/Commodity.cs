using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Commodity
{
    public Guid CommodityId { get; set; }

    public Guid TenantId { get; set; }

    public string CommodityCode { get; set; } = null!;

    public string? HsCode { get; set; }

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public bool IsDangerous { get; set; }

    public string? ImdgClassCode { get; set; }

    public string? UnNumber { get; set; }

    public string? PackingGroup { get; set; }

    public bool IsTemperatureControlled { get; set; }

    public decimal? DefaultMinTempC { get; set; }

    public decimal? DefaultMaxTempC { get; set; }

    public string? LegacyImoCode { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
