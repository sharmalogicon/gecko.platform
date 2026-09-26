using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class CodeMapping
{
    public Guid CodeMappingId { get; set; }

    public Guid TenantId { get; set; }

    public string MappingType { get; set; } = null!;

    public string? CodeListCategory { get; set; }

    public Guid? PartyId { get; set; }

    public string Channel { get; set; } = null!;

    public string Direction { get; set; } = null!;

    public string ExternalCode { get; set; } = null!;

    public string InternalCode { get; set; } = null!;

    public Guid? InternalId { get; set; }

    public string? Description { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
