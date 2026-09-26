using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class ShippingLineExtension
{
    public Guid PartyId { get; set; }

    public Guid TenantId { get; set; }

    public string LineRole { get; set; } = null!;

    public Guid? PrincipalLinePartyId { get; set; }

    public string? ScacCode { get; set; }

    public string? SmdgCode { get; set; }

    public string? OperatorCode { get; set; }

    public string? ImoCompanyNo { get; set; }

    public string? AllianceCode { get; set; }

    public string? AllianceName { get; set; }

    public string? EdiPartnerCode { get; set; }

    public bool EdiSupportsCoparn { get; set; }

    public bool EdiSupportsCodeco { get; set; }

    public bool EdiSupportsCoarri { get; set; }

    public bool EdiSupportsBaplie { get; set; }

    public string? BrandColorHex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
