using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class SurveyDamage
{
    public Guid SurveyDamageId { get; set; }

    public Guid TenantId { get; set; }

    public Guid SurveyId { get; set; }

    public short LineNo { get; set; }

    public string? DamageLocationCode { get; set; }

    public string? ComponentCode { get; set; }

    public string DamageCode { get; set; } = null!;

    public decimal? LengthCm { get; set; }

    public decimal? WidthCm { get; set; }

    public short Quantity { get; set; }

    public bool IsPreExisting { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
