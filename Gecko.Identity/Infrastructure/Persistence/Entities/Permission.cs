using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Permission
{
    public string PermissionCode { get; set; } = null!;

    public string ModuleCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
