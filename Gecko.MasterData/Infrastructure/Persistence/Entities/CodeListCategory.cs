using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CodeListCategory
{
    public string CategoryCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string OwningModule { get; set; } = null!;

    public bool AllowsTenantValues { get; set; }

    public string? LegacyVectorCategory { get; set; }

    public bool IsActive { get; set; }
}
