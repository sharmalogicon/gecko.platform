using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Module
{
    public string ModuleCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsBranchScoped { get; set; }

    public bool RequiresOnboarding { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
