using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Module
{
    public string ModuleCode { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public string ContextCode { get; set; } = null!;

    public bool IsLicensable { get; set; }

    public bool IsOperational { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }
}
