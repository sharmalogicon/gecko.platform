using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class ApiToken
{
    public Guid ApiTokenId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    public string? ModuleCode { get; set; }

    public string Name { get; set; } = null!;

    public string TokenPrefix { get; set; } = null!;

    public byte[] TokenHash { get; set; } = null!;

    public string? ScopesJson { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
