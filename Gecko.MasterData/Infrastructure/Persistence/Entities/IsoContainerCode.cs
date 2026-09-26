using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class IsoContainerCode
{
    public string IsoCode { get; set; } = null!;

    public string Standard { get; set; } = null!;

    public string? LengthCode { get; set; }

    public string? HeightCode { get; set; }

    public string? TypeCode { get; set; }

    public string GroupCode { get; set; } = null!;

    public decimal LengthFt { get; set; }

    public int? HeightMm { get; set; }

    public decimal Teu { get; set; }

    public bool IsReefer { get; set; }

    public bool IsHighCube { get; set; }

    public bool IsOpenTop { get; set; }

    public bool IsPlatform { get; set; }

    public bool IsTank { get; set; }

    public string DescriptionEn { get; set; } = null!;

    public string? SupersededBy { get; set; }

    public string? MappingNote { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
