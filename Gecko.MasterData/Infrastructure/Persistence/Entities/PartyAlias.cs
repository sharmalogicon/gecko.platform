using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class PartyAlias
{
    public Guid PartyAliasId { get; set; }

    public Guid TenantId { get; set; }

    public Guid PartyId { get; set; }

    public string AliasType { get; set; } = null!;

    public string AliasValue { get; set; } = null!;

    public string? AliasLabel { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsPrimaryForType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
