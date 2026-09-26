using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CargoClass
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public bool RequiresTempControl { get; set; }

    public bool RequiresImdgHandling { get; set; }

    public bool RequiresOogHandling { get; set; }

    public bool IsEmptyRepositioning { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
