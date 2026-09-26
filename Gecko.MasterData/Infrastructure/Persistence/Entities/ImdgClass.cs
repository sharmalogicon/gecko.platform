using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class ImdgClass
{
    public string ClassCode { get; set; } = null!;

    public byte ClassNo { get; set; }

    public string NameEn { get; set; } = null!;

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
