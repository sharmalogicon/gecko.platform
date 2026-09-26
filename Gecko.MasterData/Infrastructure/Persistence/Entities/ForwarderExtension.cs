using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class ForwarderExtension
{
    public Guid PartyId { get; set; }

    public Guid TenantId { get; set; }

    public bool IsCustomsBroker { get; set; }

    public string? CustomsLicenceNo { get; set; }

    public bool AeoCertified { get; set; }

    public string? AeoCertificateNo { get; set; }

    public DateOnly? AeoExpiryDate { get; set; }

    public string? IataCode { get; set; }

    public string? FiataMemberNo { get; set; }

    public Guid? PreferredCustomsBrokerPartyId { get; set; }

    public string? DefaultIncoterm { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
