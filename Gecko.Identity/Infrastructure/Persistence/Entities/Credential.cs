using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Credential
{
    public Guid CredentialId { get; set; }

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public byte[] PasswordHash { get; set; } = null!;

    public string Algorithm { get; set; } = null!;

    public string? AlgorithmParams { get; set; }

    public bool MustChange { get; set; }

    public bool IsCurrent { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
