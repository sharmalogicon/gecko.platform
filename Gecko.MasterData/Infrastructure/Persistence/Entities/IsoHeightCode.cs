using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class IsoHeightCode
{
    public string HeightCode { get; set; } = null!;

    public int? HeightMm { get; set; }

    public string Label { get; set; } = null!;

    public bool IsHighCube { get; set; }

    public bool IsHalfHeight { get; set; }
}
