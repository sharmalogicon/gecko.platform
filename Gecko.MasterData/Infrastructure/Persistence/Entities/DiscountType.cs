using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class DiscountType
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
