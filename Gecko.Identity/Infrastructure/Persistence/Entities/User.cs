using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class User
{
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public string Email { get; set; } = null!;

    public string EmailNormalised { get; set; } = null!;

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public string FullName { get; set; } = null!;

    public string? Phone { get; set; }

    public string? JobTitle { get; set; }

    public string UserType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public Guid? DefaultBranchId { get; set; }

    public string? Locale { get; set; }

    public string? Timezone { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
