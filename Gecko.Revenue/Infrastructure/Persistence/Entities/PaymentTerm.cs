using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class PaymentTerm
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public bool SettlesBeforeRelease { get; set; }

    public bool RequiresCreditAccount { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset ReplicatedAt { get; set; }
}
