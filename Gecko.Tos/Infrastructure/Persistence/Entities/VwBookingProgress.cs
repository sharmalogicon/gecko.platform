using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwBookingProgress
{
    public Guid BookingId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string OrderNo { get; set; } = null!;

    public string OrderTypeCode { get; set; } = null!;

    public string LinePartyCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public int QtyRequired { get; set; }

    public int QtyAssigned { get; set; }

    public int QtyCompleted { get; set; }

    public int? QtyOpen { get; set; }

    public int StepsDone { get; set; }

    public int StepsPendingRequired { get; set; }

    public string ProgressStatus { get; set; } = null!;
}
