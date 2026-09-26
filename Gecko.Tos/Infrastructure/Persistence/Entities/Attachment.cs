using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class Attachment
{
    public Guid AttachmentId { get; set; }

    public Guid TenantId { get; set; }

    public string OwnerType { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public string BlobUri { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long? SizeBytes { get; set; }

    public byte[]? Sha256 { get; set; }

    public string? Caption { get; set; }

    public DateTimeOffset? TakenAt { get; set; }

    public Guid? TakenBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
