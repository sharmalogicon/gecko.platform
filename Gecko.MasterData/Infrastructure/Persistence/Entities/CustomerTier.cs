using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CustomerTier
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public byte Priority { get; set; }

    public string? DisplayColorHex { get; set; }

    public short? DefaultCreditTermDays { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
