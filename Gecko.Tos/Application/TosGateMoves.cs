using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosGateMoves"/>: read under the caller's tenant (RLS).</summary>
internal sealed class TosGateMoves(TosDbContext db) : ITosGateMoves
{
    public async Task<IReadOnlyList<TosGateMove>> MovesAsync(
        Guid branchId, DateTimeOffset from, DateTimeOffset to, TosGateMoveFilter filter, CancellationToken ct)
    {
        var moves = db.GateTransactions.AsNoTracking()
            .Where(g => g.BranchId == branchId && g.TransactionAt >= from && g.TransactionAt < to && g.Status == "COMPLETED" && g.DeletedAt == null);
        if (filter.MovementCode is { } movement) moves = moves.Where(g => g.MovementCode == movement);
        if (filter.LineCode is { } line) moves = moves.Where(g => g.LinePartyCode == line);

        var rows = from g in moves
                   join b in db.Bookings on g.BookingId equals b.BookingId
                   select new { g, b.BookingTypeCode, b.OrderTypeCode };
        if (filter.BookingTypeCode is { } type) rows = rows.Where(r => r.BookingTypeCode == type);
        if (filter.OrderTypeCode is { } order) rows = rows.Where(r => r.OrderTypeCode == order);

        return await rows
            .OrderBy(r => r.g.TransactionAt)
            .Select(r => new TosGateMove(r.g.GateTransactionId, r.g.EirNo, r.g.TransactionAt, r.g.MovementCode, r.g.FullEmpty,
                r.g.ContainerNo, r.g.EquipmentTypeCode, r.g.BookingId, r.g.BookingContainerId, r.g.LinePartyCode,
                r.BookingTypeCode, r.OrderTypeCode, r.g.TruckVisitId, r.g.Direction))
            .ToListAsync(ct);
    }
}
