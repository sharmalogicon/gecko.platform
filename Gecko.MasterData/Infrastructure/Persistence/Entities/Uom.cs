using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Uom
{
    public string UomCode { get; set; } = null!;

    public string Category { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? Symbol { get; set; }

    public string? BaseUomCode { get; set; }

    public decimal? FactorToBase { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
