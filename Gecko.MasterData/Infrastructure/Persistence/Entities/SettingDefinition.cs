using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class SettingDefinition
{
    public string SettingKey { get; set; } = null!;

    public string ValueType { get; set; } = null!;

    public string? DefaultValue { get; set; }

    public string AllowedScope { get; set; } = null!;

    public string OwningModule { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public bool IsActive { get; set; }
}
