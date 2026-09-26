using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Currency
{
    public string CurrencyCode { get; set; } = null!;

    public string NumericCode { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public byte? MinorUnits { get; set; }

    public string? Symbol { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset ReplicatedAt { get; set; }
}
