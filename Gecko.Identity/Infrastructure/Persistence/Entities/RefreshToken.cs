using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class RefreshToken
{
    public Guid TokenId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public byte[] TokenHash { get; set; } = null!;

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? ReplacedById { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
