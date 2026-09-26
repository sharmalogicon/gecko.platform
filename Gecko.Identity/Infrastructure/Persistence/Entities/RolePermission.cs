using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class RolePermission
{
    public Guid RolePermissionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public string PermissionCode { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
