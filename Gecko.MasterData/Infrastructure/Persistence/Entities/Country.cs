using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Country
{
    public string CountryCode { get; set; } = null!;

    public string Iso3Code { get; set; } = null!;

    public string NumericCode { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? OfficialNameEn { get; set; }

    public string? NameLocal { get; set; }

    public string? DefaultCurrency { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
