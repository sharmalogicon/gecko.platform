using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class FreeTimeRule
{
    public Guid FreeTimeRuleId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ScheduleId { get; set; }

    public string FreeTimeKind { get; set; } = null!;

    public string? FullEmpty { get; set; }

    public string? Direction { get; set; }

    public string? CargoGroup { get; set; }

    public string? EquipmentSize { get; set; }

    public short FreeUnits { get; set; }

    public string Unit { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
