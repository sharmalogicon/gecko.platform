using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class IsoLengthCode
{
    public string LengthCode { get; set; } = null!;

    public int LengthMm { get; set; }

    public decimal LengthFt { get; set; }

    public string Label { get; set; } = null!;
}
