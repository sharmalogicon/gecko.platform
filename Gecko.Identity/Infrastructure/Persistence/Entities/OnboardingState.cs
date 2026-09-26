using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class OnboardingState
{
    public Guid OnboardingId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    public string ModuleCode { get; set; } = null!;

    public int CurrentStep { get; set; }

    public int TotalSteps { get; set; }

    public string? WizardDataJson { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
