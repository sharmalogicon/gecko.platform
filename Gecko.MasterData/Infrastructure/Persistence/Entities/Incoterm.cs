using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Incoterm
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string TransportMode { get; set; } = null!;

    public string Edition { get; set; } = null!;

    public bool SellerPaysMainCarriage { get; set; }

    public bool SellerPaysInsurance { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
