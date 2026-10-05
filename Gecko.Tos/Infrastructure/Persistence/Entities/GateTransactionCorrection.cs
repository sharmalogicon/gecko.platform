using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

/// <summary>gecko_tos 26: one audited after-Save correction of an EIR (append-only by grant).</summary>
public partial class GateTransactionCorrection
{
    public Guid GateTransactionCorrectionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid GateTransactionId { get; set; }

    public string Reason { get; set; } = null!;

    public string BeforeJson { get; set; } = null!;

    public string AfterJson { get; set; } = null!;

    public DateTimeOffset CorrectedAt { get; set; }

    public Guid CorrectedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
