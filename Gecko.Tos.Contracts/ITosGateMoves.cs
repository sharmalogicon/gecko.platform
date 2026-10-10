namespace Gecko.Tos.Contracts;

/// <summary>
/// A depot's completed gate moves over a window, with the booking they were made for — what Vector's accounting
/// reports read from Operation.ContainerMovement (Lift Off - Washing, the storage and lift summaries). Revenue prices
/// them; only TOS knows them.
/// </summary>
public interface ITosGateMoves
{
    /// <summary>Completed (not voided) moves at <paramref name="branchId"/> with transaction_at in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    Task<IReadOnlyList<TosGateMove>> MovesAsync(Guid branchId, DateTimeOffset from, DateTimeOffset to, TosGateMoveFilter filter, CancellationToken ct);
}

/// <param name="MovementCode">Gecko's code (MTY_IN, FULL_OUT …); null = every movement.</param>
/// <param name="LineCode">The shipping line the move was made for (Vector's AgentCode).</param>
public sealed record TosGateMoveFilter(
    string? MovementCode = null, string? LineCode = null, string? BookingTypeCode = null, string? OrderTypeCode = null);

/// <param name="Direction">IN or OUT — with FullEmpty, the move whatever the tenant's movement codes (MTY_OUT, GOE …).</param>
/// <param name="EquipmentTypeCode">As the gate recorded the box ("20GP"); null when it was not recorded.</param>
public sealed record TosGateMove(
    Guid GateTransactionId, string EirNo, DateTimeOffset TransactionAt, string MovementCode, string FullEmpty,
    string ContainerNo, string? EquipmentTypeCode, Guid BookingId, Guid BookingContainerId, string LineCode,
    string BookingTypeCode, string OrderTypeCode, Guid TruckVisitId, string Direction);
