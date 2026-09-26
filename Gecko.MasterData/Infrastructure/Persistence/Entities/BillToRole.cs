using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class BillToRole
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string? LegacyVectorCode { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }
}
