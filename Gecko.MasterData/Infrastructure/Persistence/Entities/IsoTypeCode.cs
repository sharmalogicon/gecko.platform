using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class IsoTypeCode
{
    public string TypeCode { get; set; } = null!;

    public string GroupCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;
}
