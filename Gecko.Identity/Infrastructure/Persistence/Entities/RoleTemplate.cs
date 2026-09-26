using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class RoleTemplate
{
    public string RoleCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsTenantAdmin { get; set; }

    public bool IsAssignable { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
