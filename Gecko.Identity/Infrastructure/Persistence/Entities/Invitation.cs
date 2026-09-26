using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Invitation
{
    public Guid InvitationId { get; set; }

    public Guid TenantId { get; set; }

    public string Email { get; set; } = null!;

    public string EmailNormalised { get; set; } = null!;

    public string? FullName { get; set; }

    public Guid RoleId { get; set; }

    public Guid? BranchId { get; set; }

    public byte[] TokenHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public int ResentCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
