using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class Location
{
    public Guid LocationId { get; set; }

    public Guid TenantId { get; set; }

    public string LocationCode { get; set; } = null!;

    public string LocationNameEn { get; set; } = null!;

    public string? LocationNameLocal { get; set; }

    public string LocationType { get; set; } = null!;

    public string? AreaCode { get; set; }

    public Guid? PartyId { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Postcode { get; set; }

    public string? CountryCode { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
