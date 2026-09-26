using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class IsoTypeGroup
{
    public string GroupCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public bool IsReefer { get; set; }

    public bool IsInsulated { get; set; }

    public bool IsOpenTop { get; set; }

    public bool IsPlatform { get; set; }

    public bool IsTank { get; set; }

    public bool IsDangerousCapable { get; set; }

    public string? DefaultCargoClass { get; set; }
}
