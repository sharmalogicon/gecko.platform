using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Movement
{
    public Guid MovementId { get; set; }

    public Guid TenantId { get; set; }

    public string MovementCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string FullEmpty { get; set; } = null!;

    public string Direction { get; set; } = null!;

    public string AppliesToModule { get; set; } = null!;

    public string? CodecoStatusCode { get; set; }

    public bool ChangesYardPosition { get; set; }

    public bool ChangesStatus { get; set; }

    public bool RequiresSurvey { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
