using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class UserRole
{
    public Guid UserRoleId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public Guid? BranchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
