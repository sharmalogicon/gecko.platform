using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class VwCodeList
{
    public string CategoryCode { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string? DescriptionEn { get; set; }

    public string? DescriptionLocal { get; set; }

    public string? IsoCode { get; set; }

    public short? SortOrder { get; set; }

    public bool? IsActive { get; set; }

    public bool? IsTenantDefined { get; set; }
}
