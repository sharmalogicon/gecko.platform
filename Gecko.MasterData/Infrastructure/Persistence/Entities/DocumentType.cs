using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class DocumentType
{
    public string Code { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string DocumentCategory { get; set; } = null!;

    public bool IsLegalDocument { get; set; }

    public bool RequiresSignature { get; set; }

    public short? RetentionYears { get; set; }

    public string? EdiMessageType { get; set; }

    public string? DefaultFormat { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; }
}
