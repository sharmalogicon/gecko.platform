using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CodeListValue1
{
    public string CategoryCode { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? IsoCode { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }
}
