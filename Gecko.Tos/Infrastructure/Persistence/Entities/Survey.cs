using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class Survey
{
    public Guid SurveyId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid ContainerVisitId { get; set; }

    public Guid? GateTransactionId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public string SurveyType { get; set; } = null!;

    public DateTimeOffset SurveyedAt { get; set; }

    public Guid? SurveyedBy { get; set; }

    public string? SurveyorName { get; set; }

    public string? ConditionCode { get; set; }

    public string? GradeCode { get; set; }

    public bool IsServiceable { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
