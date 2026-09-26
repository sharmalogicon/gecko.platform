using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Inbox
{
    public long InboxId { get; set; }

    public Guid TenantId { get; set; }

    public string SourceContext { get; set; } = null!;

    public long MessageId { get; set; }

    public string MessageType { get; set; } = null!;

    public Guid? AggregateId { get; set; }

    public string Outcome { get; set; } = null!;

    public string? Note { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
