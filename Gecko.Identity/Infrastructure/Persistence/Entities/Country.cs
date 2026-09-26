using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Country
{
    public string CountryCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string DefaultTimezone { get; set; } = null!;

    public string DefaultLocale { get; set; } = null!;

    public string DefaultCurrency { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
