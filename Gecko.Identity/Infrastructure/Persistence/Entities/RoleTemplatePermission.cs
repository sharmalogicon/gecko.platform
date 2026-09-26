using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class RoleTemplatePermission
{
    public string RoleCode { get; set; } = null!;

    public string PermissionCode { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
